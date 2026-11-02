using System.Runtime.InteropServices;

namespace LtcMtc;

public enum Tone { Neutral, Good, Warning }

/// <summary>What the UI shows. Polled on the UI thread while real-time threads write; a torn read only shows for 30 ms.</summary>
public sealed record EngineView(string Status, Tone Tone, string Timecode, string[] Summary, (string Label, string Value)[] Rows);

/// <summary>Both offsets, swapped as one object so the real-time threads never see half an update.</summary>
public sealed record Offsets(double Seconds, TimecodeOffset Tc)
{
    public static Offsets From(Settings s) =>
        new(s.OffsetMs / 1000, TimecodeOffset.TryParse(s.TcOffset, out var o) ? o : TimecodeOffset.Zero);
}

static class Fmt
{
    public static string Tc(long frame, FrameRate r) => Timecode.FromFrames(frame, r).ToString();
    public static string Db(float v) => v < 1e-5f ? "−∞ dBFS" : $"{20 * MathF.Log10(v):0.0} dBFS";
    public static double Ema(double avg, double x) => avg + (x - avg) * 0.05;

    /// <summary>How long input may go quiet before it counts as stopped: the user's freewheel time on top of the normal
    /// gap between frames (two frames, plus 50 ms for audio buffering). Freewheel 0 thus means "stop as soon as the
    /// input stops", not "never start".</summary>
    public static double Timeout(double freewheel, FrameRate r) => freewheel + 2 / r.Fps() + 0.05;
}

// ───────────────────────────────────────────── Sources ─────────────────────────────────────────────

/// <summary>Where the timeline comes from: it publishes <see cref="Anchor"/>s and says whether it is running.</summary>
public abstract class Source : IDisposable
{
    protected double freewheel;

    public abstract Anchor? Anchor { get; }
    /// <summary>Running now, or within the freewheel time of the last input.</summary>
    public abstract bool Live { get; }
    /// <summary>The frame to show: moving while live, held when stopped.</summary>
    public abstract long? Frame { get; }
    public abstract (string Status, Tone Tone) State { get; }
    public abstract IEnumerable<(string, string)> Rows();
    public abstract IEnumerable<string> Summary();
    public virtual string? Fault => null;
    public virtual void Start() { }
    public virtual void Dispose() { }

    protected bool LiveSince(double lastInput) => Anchor is { } a && Clock.Now - lastInput < Fmt.Timeout(freewheel, a.Rate);
}

/// <summary>MTC from a MIDI input (MMC is read too, for display).</summary>
sealed class MtcSource : Source
{
    readonly MidiInput midi;
    readonly MtcReader mtc = new();
    volatile Anchor? anchor;
    double jitterMs;

    public MtcSource(Settings s)
    {
        freewheel = s.FreewheelMs / 1000.0;
        mtc.Anchored = OnAnchor;
        midi = new MidiInput(s.MidiIn, (msg, t) => { if ((msg & 0xFF) == 0xF1) mtc.QuarterFrame(msg >> 8 & 0x7F, t); }, mtc.SysEx);
    }

    void OnAnchor(Anchor a, bool locate)
    {
        var prev = anchor;
        if (!locate && prev != null && prev.Rate == a.Rate && a.Frame - prev.Frame is > 0 and < 8)
            jitterMs = Fmt.Ema(jitterMs, Math.Abs(a.Time - prev.Time - (a.Frame - prev.Frame) / a.Rate.Fps()) * 1000);
        anchor = a;
    }

    public override Anchor? Anchor => anchor;
    public override bool Live => LiveSince(mtc.LastMessage);
    public override long? Frame => anchor is { } a ? (long)Math.Floor(a.FrameAt(Live ? Clock.Now : mtc.LastMessage)) : null;

    public override (string, Tone) State =>
        Live ? Clock.Now - mtc.LastMessage < 0.1 ? ("Locked", Tone.Good) : ("Freewheel", Tone.Warning)
        : anchor == null ? ("Waiting for MTC", Tone.Neutral) : ("Stopped", Tone.Neutral);

    string? MmcText => mtc.LastMmc is { } c
        ? Mmc.Name(c) + (mtc.MmcLocate is var (tc, _) ? $" · locate {tc}" : "")
        : null;

    public override IEnumerable<(string, string)> Rows()
    {
        var a = anchor;
        yield return ("MTC in", a == null ? "—" : $"{Fmt.Tc(a.Frame, a.Rate)}  ·  {a.Rate.Label()}");
        yield return ("MTC jitter", $"{jitterMs:0.00} ms");
        yield return ("Quarter-frames in", mtc.QuarterFrames.ToString("N0"));
        yield return ("Sequence errors", mtc.SequenceErrors.ToString("N0"));
        yield return ("MMC in", MmcText is { } m ? $"{m}  ({mtc.MmcCommands:N0})" : "—");
    }

    public override IEnumerable<string> Summary()
    {
        yield return anchor is { } a ? $"MTC · {a.Rate.Label()}" : "MTC";
        yield return $"jitter {jitterMs:0.00} ms";
        if (MmcText is { } m) yield return "MMC " + m;
    }

    public override string? Fault => midi.Fault;
    public override void Dispose() => midi.Dispose();
}

/// <summary>LTC from an audio input. Each frame is stamped with the sample-accurate time its successor starts.</summary>
sealed class LtcSource : Source
{
    readonly AudioStream audio;
    readonly LtcDecoder dec;
    readonly LtcTimeline timeline = new();
    volatile Anchor? anchor;
    double blockTime, lastFrame = double.NegativeInfinity, jitterMs, fps;
    long blockStart, framesIn, invalid;

    public LtcSource(Settings s)
    {
        freewheel = s.FreewheelMs / 1000.0;
        audio = Audio.Input(s.AudioIn, s.InChannel, s.BufferMs, s.LowLatency, Capture);
        dec = new LtcDecoder(audio.SampleRate) { FrameDecoded = OnFrame };
    }

    void Capture(ReadOnlySpan<float> mono, double time)
    {
        (blockTime, blockStart) = (time, dec.SamplesProcessed);
        dec.Process(mono);
    }

    void OnFrame(ulong bits, double sample, double measured)
    {
        var tc = LtcFrame.Unpack(bits, out bool drop);
        var r = FrameRates.FromMeasured(measured, drop);
        if (tc.H > 23 || tc.M > 59 || tc.S > 59 || tc.F >= r.Nominal()) { invalid++; return; }
        var a = new Anchor(tc.ToFrames(r) + 1, blockTime + (sample - blockStart) / audio.SampleRate, r);  // the next frame starts here
        framesIn++;
        var prev = timeline.Current;
        if (!timeline.Offer(a)) return;   // one odd frame: wait for the next to confirm it before moving the timeline
        if (prev != null && a.Frame == prev.Frame + 1)
            jitterMs = Fmt.Ema(jitterMs, Math.Abs(a.Time - prev.Time - 1 / measured) * 1000);
        (fps, lastFrame, anchor) = (measured, Clock.Now, a);
    }

    public override Anchor? Anchor => anchor;
    public override bool Live => LiveSince(lastFrame);
    public override long? Frame => anchor is { } a ? Live ? (long)Math.Floor(a.FrameAt(Clock.Now)) : a.Frame - 1 : null;

    public override (string, Tone) State =>
        Live ? Clock.Now - lastFrame < 0.12 ? ("Locked", Tone.Good) : ("Freewheel", Tone.Warning)
        : framesIn == 0 ? ("Waiting for LTC", Tone.Neutral) : ("No signal", Tone.Neutral);

    public override IEnumerable<(string, string)> Rows()
    {
        var a = anchor;
        yield return ("LTC in", a == null ? "—" : $"{Fmt.Tc(a.Frame - 1, a.Rate)}  ·  {a.Rate.Label()}");
        yield return ("Measured rate", framesIn == 0 ? "—" : $"{fps:0.000} fps");
        yield return ("Signal", Fmt.Db(dec.Envelope));
        yield return ("LTC jitter", $"{jitterMs:0.00} ms");
        yield return ("Frames decoded", framesIn.ToString("N0"));
        yield return ("Jumps / rejected", $"{timeline.Jumps:N0} / {timeline.Rejected + invalid:N0}");
        yield return ("Audio in", audio.Info);
    }

    public override IEnumerable<string> Summary()
    {
        yield return anchor is { } a ? $"LTC · {a.Rate.Label()}" : "LTC";
        yield return "signal " + Fmt.Db(dec.Envelope);
        yield return $"jitter {jitterMs:0.00} ms";
    }

    public override string? Fault => audio.Fault;
    public override void Start() => audio.Start();
    public override void Dispose() => audio.Dispose();
}

/// <summary>The internal clock. It outlives engines, so a device reconnect doesn't lose the position.</summary>
sealed class GeneratorSource(GeneratorClock clock) : Source
{
    public GeneratorClock Clock_ => clock;
    public override Anchor? Anchor => clock.Current.Anchor;
    public override bool Live => clock.Current.Playing;
    public override long? Frame => clock.FrameAt(Clock.Now);
    public override (string, Tone) State => Live ? ("Playing", Tone.Good) : ("Paused", Tone.Neutral);

    public override IEnumerable<(string, string)> Rows()
    {
        yield return ("Generator", $"{clock.Rate.Label()}  ·  start {Fmt.Tc(clock.Start, clock.Rate)}");
    }

    public override IEnumerable<string> Summary()
    {
        yield return $"Generator · {clock.Rate.Label()}";
    }
}

// ───────────────────────────────────────────── Outputs ─────────────────────────────────────────────

/// <summary>Where the timeline goes. Reads the source's anchor from its own real-time thread.</summary>
public abstract class Sink(Source source) : IDisposable
{
    protected readonly Source src = source;
    protected volatile Offsets offsets = new(0, TimecodeOffset.Zero);

    public Offsets Offsets { set => offsets = value; }
    /// <summary>The frame being output right now, or null while idle.</summary>
    public abstract (long Frame, FrameRate Rate)? Output { get; }
    public abstract IEnumerable<(string, string)> Rows();
    public abstract string Name { get; }
    public virtual string? Fault => null;
    public virtual void Start() { }
    public virtual void Dispose() { }
}

/// <summary>
/// LTC out. At every LTC frame boundary the audio thread works out where the timeline will be when that boundary is
/// heard. Small errors are steered out by nudging the encoder's speed (a PI loop, at most ±0.5 %); anything over a frame
/// jams to the new position.
/// </summary>
sealed class LtcSink : Sink
{
    readonly AudioStream audio;
    readonly LtcEncoder enc;
    readonly FrameRate? fixedRate;
    double blockTime, ppm, integral;
    long relocates, framesOut;

    public LtcSink(Settings s, Source source) : base(source)
    {
        offsets = Offsets.From(s);
        fixedRate = source is GeneratorSource ? null : s.OutRate;
        audio = Audio.Output(s.AudioOut, s.OutChannel, s.BufferMs, s.LowLatency, Render);
        enc = new LtcEncoder(audio.SampleRate) { Level = MathF.Pow(10, (float)s.LevelDb / 20), FrameStart = OnFrameStart };
    }

    void Render(Span<float> mono, double time)
    {
        if (enc == null) { mono.Clear(); return; }   // a pre-roll request before the encoder exists
        blockTime = time;
        enc.Render(mono);
    }

    void OnFrameStart(int index)
    {
        var e = enc;
        var a = src.Anchor;
        if (a == null || !src.Live) { (e.Active, e.Speed) = (false, 1); return; }

        var rate = fixedRate ?? a.Rate;
        double spf = audio.SampleRate / rate.Fps();
        var o = offsets;
        double t = blockTime + (double)index / audio.SampleRate + o.Seconds;   // positive offset: the output runs ahead
        double target = (a.Seconds + (t - a.Time)) * rate.Fps() + o.Tc.Frames(rate);   // the timeline at t, in output frames
        double err = target - (e.Frame + e.Pos / spf);
        if (!e.Active || e.Rate != rate || Math.Abs(err) > 1)
        {
            long f = (long)Math.Floor(target);
            (e.Frame, e.Pos, e.Speed, e.Rate, integral) = (f, (target - f) * spf, 1, rate, 0);
            if (e.Active) relocates++;
        }
        else
        {
            // PI loop, tuned in simulation: ±1 ms MTC jitter + 200 ppm clock drift → <0.25 ms error, ~160 ppm speed noise.
            integral = Math.Clamp(integral + err * 0.0003, -0.005, 0.005);
            e.Speed = 1 + Math.Clamp(integral + err * 0.01, -0.005, 0.005);
        }
        ppm = Fmt.Ema(ppm, (e.Speed - 1) * 1e6);
        e.Active = true;
        framesOut++;
    }

    public override (long, FrameRate)? Output => enc.Active ? (enc.Frame, enc.Rate) : null;
    public override string Name => "LTC";

    public override IEnumerable<(string, string)> Rows()
    {
        yield return ("LTC out", enc.Active ? enc.Rate.Label() : "—");
        yield return ("Clock correction", $"{ppm:+0;-0;0} ppm");
        yield return ("Frames out", framesOut.ToString("N0"));
        yield return ("Relocates", relocates.ToString("N0"));
        yield return ("Output level", Fmt.Db(audio.Peak));
        yield return ("Audio out", audio.Info);
    }

    public override string? Fault => audio.Fault;
    public override void Start() => audio.Start();
    public override void Dispose() => audio.Dispose();
}

/// <summary>
/// MTC and/or MMC out. A high-priority thread on a high-resolution waitable timer sends each quarter-frame when it falls
/// due (typically within ~0.1 ms); starts and jumps send a full-frame message and/or MMC (locate +) play, stops send
/// MMC stop. With the generator, a locate while paused cues receivers with a full-frame / MMC locate.
/// </summary>
sealed class MidiSink : Sink
{
    readonly MidiOutput midi;
    readonly Thread thread;
    readonly bool sendMtc, sendMmc, mmcLocate, mmcRateBits, cueWhenIdle;
    readonly byte mmcDevice, mmcPlay;
    volatile bool stop, running;
    long qfOut, locates, mmcOut, outFrame;
    FrameRate outRate;

    public MidiSink(Settings s, Source source) : base(source)
    {
        offsets = Offsets.From(s);
        (sendMtc, sendMmc, mmcDevice) = (s.SendMtc, s.SendMmc, (byte)Math.Clamp(s.MmcDevice, 0, 127));
        (mmcLocate, mmcRateBits, mmcPlay) = (s.MmcLocate, s.MmcRateBits, s.MmcDeferredPlay ? Mmc.DeferredPlay : Mmc.Play);
        cueWhenIdle = source is GeneratorSource;
        midi = new MidiOutput(s.MidiOut);
        thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "MTC out" };
    }

    public override void Start() => thread.Start();

    void Run()
    {
        using var timer = new HiResTimer();
        long q = 0;
        Anchor? idle = null;
        while (!stop)
        {
            var a = src.Anchor;
            double wait = 0.002;
            if (a != null && src.Live)
            {
                var o = offsets;
                double pos = a.FrameAt(Clock.Now + o.Seconds) + o.Tc.Frames(a.Rate);
                long target = (long)Math.Floor(pos * 4);
                if (!running || a.Rate != outRate || target < q || target - q > 8)
                {
                    // Start or jump: MTC full-frame, and/or MMC (locate +) play.
                    var tc = Timecode.FromFrames((long)Math.Floor(pos), a.Rate);
                    if (sendMtc) midi.SendSysEx(Mtc.FullFrame(tc, a.Rate));
                    if (sendMmc && mmcLocate) Transport(Mmc.Locate(mmcDevice, tc, a.Rate, mmcRateBits));
                    if (sendMmc) Transport(Mmc.Command(mmcDevice, mmcPlay));
                    (q, outRate, running) = (target, a.Rate, true);
                    locates++;
                }
                // Quarter-frame q is piece q mod 8 of the two-frame sequence that starts at frame (q div 8) * 2.
                for (; q < target; q++)
                {
                    if (!sendMtc) continue;
                    midi.Send(Mtc.QuarterFrame((int)((q + 1) & 7), Timecode.FromFrames((q + 1) >> 3 << 1, outRate), outRate));
                    qfOut++;
                }
                outFrame = (long)Math.Floor(pos);
                wait = Math.Min(wait, ((q + 1) / 4.0 - pos) / outRate.Fps());
            }
            else if (running)
            {
                (running, idle) = (false, cueWhenIdle ? null : a);   // generator: cue receivers to where it stopped
                if (sendMmc) Transport(Mmc.Command(mmcDevice, Mmc.Stop));   // input stopped (after the freewheel time)
            }
            else if (cueWhenIdle && a != null && !ReferenceEquals(a, idle))
            {
                idle = a;   // the generator stopped, or was located while paused: cue receivers to the position
                var tc = Timecode.FromFrames(a.Frame + offsets.Tc.Frames(a.Rate), a.Rate);
                if (sendMtc) midi.SendSysEx(Mtc.FullFrame(tc, a.Rate));
                if (sendMmc && mmcLocate) Transport(Mmc.Locate(mmcDevice, tc, a.Rate, mmcRateBits));
            }
            timer.Wait(Math.Max(wait, 0.0001));
        }
        if (running && sendMmc) Transport(Mmc.Command(mmcDevice, Mmc.Stop));     // the app was stopped mid-play
    }

    void Transport(params byte[][] messages)
    {
        foreach (var m in messages) midi.SendSysEx(m);
        mmcOut += messages.Length;
    }

    public override (long, FrameRate)? Output => running ? (outFrame, outRate) : null;
    public override string Name => sendMmc && sendMtc ? "MTC + MMC" : sendMmc ? "MMC" : "MTC";

    public override IEnumerable<(string, string)> Rows()
    {
        yield return ("Quarter-frames out", sendMtc ? qfOut.ToString("N0") : "off");
        yield return ("Locates", locates.ToString("N0"));
        yield return ("MMC commands out", sendMmc ? mmcOut.ToString("N0") : "off");
    }

    public override string? Fault => midi.Fault;

    public override void Dispose()
    {
        stop = true;
        if (thread.IsAlive) thread.Join();
        midi.Dispose();
    }
}

// ───────────────────────────────────────────── Engine ─────────────────────────────────────────────

/// <summary>One source feeding any number of outputs (none = a reader).</summary>
public sealed class Engine : IDisposable
{
    readonly Source? source;
    readonly List<Sink> sinks = [];

    public Engine(Settings s, GeneratorClock? generator)
    {
        try
        {
            source = s.Source switch
            {
                SourceKind.Mtc => new MtcSource(s),
                SourceKind.Ltc => new LtcSource(s),
                _ => new GeneratorSource(generator ?? throw new ArgumentNullException(nameof(generator))),
            };
            if (s.OutLtc) sinks.Add(new LtcSink(s, source));
            if (s.OutMidi) sinks.Add(new MidiSink(s, source));
            foreach (var k in sinks) k.Start();
            source.Start();
        }
        catch { Dispose(); throw; }
    }

    public Offsets Offsets { set { foreach (var k in sinks) k.Offsets = value; } }

    /// <summary>Why the engine can't continue (a device went away), or null. Poll about once a second.</summary>
    public string? Fault => source?.Fault ?? sinks.Select(k => k.Fault).FirstOrDefault(f => f != null);

    public EngineView View()
    {
        var src = source!;
        var (status, tone) = src.State;
        // The big number: what an output is sending (it includes the offsets), else the input itself.
        var shown = sinks.Select(k => k.Output).FirstOrDefault(o => o != null);
        string tc = shown is var (f, r) ? Fmt.Tc(f, r) : src.Frame is { } sf && src.Anchor is { } a ? Fmt.Tc(sf, a.Rate) : "--:--:--:--";
        var summary = src.Summary().Append(sinks.Count == 0 ? "display only" : "→ " + string.Join(" + ", sinks.Select(k => k.Name)));
        return new(status, tone, tc, [.. summary], [.. src.Rows(), .. sinks.SelectMany(k => k.Rows())]);
    }

    public void Dispose()
    {
        foreach (var k in sinks) k.Dispose();   // outputs first: MIDI out still sends MMC Stop while the input winds down
        source?.Dispose();
    }
}

/// <summary>A waitable timer with ~0.5 ms resolution (Windows 10 1803+), without raising the system timer rate.</summary>
sealed partial class HiResTimer : IDisposable
{
    readonly nint h = CreateWaitableTimerExW(0, 0, 2 /* HIGH_RESOLUTION */, 0x1F0003) is var t and not 0 ? t : CreateWaitableTimerExW(0, 0, 0, 0x1F0003);

    public void Wait(double seconds)
    {
        long due = -(long)(seconds * 1e7);   // relative, in 100 ns units
        SetWaitableTimer(h, in due, 0, 0, 0, false);
        WaitForSingleObject(h, 0xFFFFFFFF);
    }

    public void Dispose() => CloseHandle(h);

    [LibraryImport("kernel32.dll")] private static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(nint h, in long due, int period, nint completion, nint arg, [MarshalAs(UnmanagedType.Bool)] bool resume);
    [LibraryImport("kernel32.dll")] private static partial uint WaitForSingleObject(nint h, uint ms);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(nint h);
}
