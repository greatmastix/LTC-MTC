using System.Runtime.InteropServices;

namespace LtcMtc;

public enum Tone { Neutral, Good, Warning }

/// <summary>What the UI shows. Polled on the UI thread while real-time threads write; a torn read only shows for 30 ms.</summary>
public sealed record EngineView(string Status, Tone Tone, string Timecode, (string Label, string Value)[] Rows);

/// <summary>Both offsets, swapped as one object so the real-time threads never see half an update.</summary>
public sealed record Offsets(double Seconds, TimecodeOffset Tc)
{
    public static Offsets From(Settings s) =>
        new(s.OffsetMs / 1000, TimecodeOffset.TryParse(s.TcOffset, out var o) ? o : TimecodeOffset.Zero);
}

public interface IEngine : IDisposable
{
    EngineView View();
    /// <summary>Change the offsets while running: big steps relocate, a few milliseconds are slewed in.</summary>
    Offsets Offsets { set; }
    /// <summary>Why the engine can't continue (a device went away), or null. Poll about once a second.</summary>
    string? Fault { get; }
}

static class Fmt
{
    public static string Tc(long frame, FrameRate r) => Timecode.FromFrames(frame, r).ToString();
    public static string Db(float v) => v < 1e-5f ? "−∞ dBFS" : $"{20 * MathF.Log10(v):0.0} dBFS";
    public static double Ema(double avg, double x) => avg + (x - avg) * 0.05;
}

/// <summary>
/// MIDI in → LTC out. The MIDI callback publishes an <see cref="Anchor"/> per complete MTC sequence; at every LTC frame
/// boundary the audio thread works out where the timeline will be when that boundary is heard. Small errors are steered
/// out by nudging the encoder's speed (a PI loop, at most ±0.5 %); anything over a frame jams to the new position.
/// </summary>
public sealed class MtcToLtc : IEngine
{
    readonly MidiInput? midi;
    readonly AudioStream? audio;
    readonly LtcEncoder? enc;
    readonly MtcReader mtc = new();
    readonly FrameRate? fixedRate;
    readonly double freewheel;
    volatile Offsets offsets;
    volatile Anchor? anchor;
    double blockTime, jitterMs, ppm, integral;
    long relocates, framesOut;

    public MtcToLtc(Settings s)
    {
        (fixedRate, freewheel, offsets) = (s.OutRate, s.FreewheelMs / 1000.0, Offsets.From(s));
        mtc.Anchored = OnAnchor;
        try
        {
            midi = new MidiInput(s.MidiIn,
                (msg, t) => { if ((msg & 0xFF) == 0xF1) mtc.QuarterFrame(msg >> 8 & 0x7F, t); }, mtc.SysEx);
            audio = Audio.Output(s.AudioOut, s.OutChannel, s.BufferMs, s.LowLatency, Render);
            enc = new LtcEncoder(audio.SampleRate) { Level = MathF.Pow(10, (float)s.LevelDb / 20), FrameStart = OnFrameStart };
            audio.Start();
        }
        catch { Dispose(); throw; }
    }

    void Render(Span<float> mono, double time)
    {
        if (enc == null) { mono.Clear(); return; }   // a pre-roll request before the encoder exists
        blockTime = time;
        enc.Render(mono);
    }

    void OnAnchor(Anchor a, bool locate)
    {
        var prev = anchor;
        if (!locate && prev != null && prev.Rate == a.Rate && a.Frame - prev.Frame is > 0 and < 8)
            jitterMs = Fmt.Ema(jitterMs, Math.Abs(a.Time - prev.Time - (a.Frame - prev.Frame) / a.Rate.Fps()) * 1000);
        anchor = a;
    }

    void OnFrameStart(int index)
    {
        var e = enc!;
        var a = anchor;
        if (a == null || Clock.Now - mtc.LastMessage > freewheel) { (e.Active, e.Speed) = (false, 1); return; }

        var rate = fixedRate ?? a.Rate;
        double spf = audio!.SampleRate / rate.Fps();
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

    public EngineView View()
    {
        var a = anchor;
        bool active = enc is { Active: true };
        var (status, tone) = active ? Clock.Now - mtc.LastMessage < 0.1 ? ("Locked", Tone.Good) : ("Freewheel", Tone.Warning)
                           : a == null ? ("Waiting for MTC", Tone.Neutral) : ("Stopped", Tone.Neutral);
        return new(status, tone, active ? Fmt.Tc(enc!.Frame, enc.Rate) : a != null ? Fmt.Tc(a.Frame, a.Rate) : "--:--:--:--",
        [
            ("MTC in", a == null ? "—" : $"{Fmt.Tc(a.Frame, a.Rate)}  ·  {a.Rate.Label()}"),
            ("LTC out", active ? enc!.Rate.Label() : "—"),
            ("MTC jitter", $"{jitterMs:0.00} ms"),
            ("Clock correction", $"{ppm:+0;-0;0} ppm"),
            ("Quarter-frames in", mtc.QuarterFrames.ToString("N0")),
            ("Sequence errors", mtc.SequenceErrors.ToString("N0")),
            ("Frames out", framesOut.ToString("N0")),
            ("Relocates", relocates.ToString("N0")),
            ("Output level", Fmt.Db(audio?.Peak ?? 0)),
            ("Audio", audio?.Info ?? "—"),
        ]);
    }

    public string? Fault => audio?.Fault ?? midi?.Fault;
    public Offsets Offsets { set => offsets = value; }

    public void Dispose() { midi?.Dispose(); audio?.Dispose(); }
}

/// <summary>
/// LTC in → MIDI out. The decoder stamps each frame with the sample-accurate time its successor starts; a high-priority
/// thread on a high-resolution waitable timer sends each quarter-frame when it falls due (typically within ~0.1 ms).
/// </summary>
public sealed class LtcToMtc : IEngine
{
    readonly MidiOutput? midi;
    readonly AudioStream? audio;
    readonly LtcDecoder? dec;
    readonly Thread? thread;
    readonly double freewheel;
    volatile Offsets offsets;
    volatile Anchor? anchor;
    volatile bool stop, running;
    double blockTime, lastFrame = double.NegativeInfinity, jitterMs, fps;
    long blockStart, framesIn, jumps, invalid, qfOut, locates, outFrame;
    FrameRate outRate;

    public LtcToMtc(Settings s)
    {
        (freewheel, offsets) = (s.FreewheelMs / 1000.0, Offsets.From(s));
        try
        {
            midi = new MidiOutput(s.MidiOut);
            audio = Audio.Input(s.AudioIn, s.InChannel, s.BufferMs, s.LowLatency, Capture);
            dec = new LtcDecoder(audio.SampleRate) { FrameDecoded = OnFrame };
            thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.Highest, Name = "MTC out" };
            thread.Start();
            audio.Start();
        }
        catch { Dispose(); throw; }
    }

    void Capture(ReadOnlySpan<float> mono, double time)
    {
        (blockTime, blockStart) = (time, dec!.SamplesProcessed);
        dec.Process(mono);
    }

    void OnFrame(ulong bits, double sample, double measured)
    {
        var tc = LtcFrame.Unpack(bits, out bool drop);
        var r = FrameRates.FromMeasured(measured, drop);
        if (tc.H > 23 || tc.M > 59 || tc.S > 59 || tc.F >= r.Nominal()) { invalid++; return; }
        var a = new Anchor(tc.ToFrames(r) + 1, blockTime + (sample - blockStart) / audio!.SampleRate, r);  // the next frame starts here
        var prev = anchor;
        if (prev != null && prev.Rate == r && a.Frame == prev.Frame + 1)
            jitterMs = Fmt.Ema(jitterMs, Math.Abs(a.Time - prev.Time - 1 / measured) * 1000);
        else if (prev != null) jumps++;
        (fps, lastFrame, anchor) = (measured, Clock.Now, a);
        framesIn++;
    }

    void Run()
    {
        using var timer = new HiResTimer();
        long q = 0;
        while (!stop)
        {
            var a = anchor;
            double wait = 0.002;
            if (a != null && Clock.Now - lastFrame < freewheel)
            {
                var o = offsets;
                double pos = a.FrameAt(Clock.Now + o.Seconds) + o.Tc.Frames(a.Rate);
                long target = (long)Math.Floor(pos * 4);
                if (!running || a.Rate != outRate || target < q || target - q > 8)
                {
                    midi!.SendSysEx(Mtc.FullFrame(Timecode.FromFrames((long)Math.Floor(pos), a.Rate), a.Rate));
                    (q, outRate, running) = (target, a.Rate, true);
                    locates++;
                }
                // Quarter-frame q is piece q mod 8 of the two-frame sequence that starts at frame (q div 8) * 2.
                while (q < target)
                {
                    q++;
                    midi!.Send(Mtc.QuarterFrame((int)(q & 7), Timecode.FromFrames(q >> 3 << 1, outRate), outRate));
                    qfOut++;
                }
                outFrame = (long)Math.Floor(pos);
                wait = Math.Min(wait, ((q + 1) / 4.0 - pos) / outRate.Fps());
            }
            else running = false;
            timer.Wait(Math.Max(wait, 0.0001));
        }
    }

    public EngineView View()
    {
        var a = anchor;
        var (status, tone) = running ? Clock.Now - lastFrame < 0.12 ? ("Locked", Tone.Good) : ("Freewheel", Tone.Warning)
                           : framesIn == 0 ? ("Waiting for LTC", Tone.Neutral) : ("No signal", Tone.Neutral);
        return new(status, tone, running ? Fmt.Tc(outFrame, outRate) : a != null ? Fmt.Tc(a.Frame - 1, a.Rate) : "--:--:--:--",
        [
            ("LTC in", a == null ? "—" : $"{Fmt.Tc(a.Frame - 1, a.Rate)}  ·  {a.Rate.Label()}"),
            ("Measured rate", framesIn == 0 ? "—" : $"{fps:0.000} fps"),
            ("Signal", Fmt.Db(dec?.Envelope ?? 0)),
            ("LTC jitter", $"{jitterMs:0.00} ms"),
            ("Frames decoded", framesIn.ToString("N0")),
            ("Jumps / invalid", $"{jumps:N0} / {invalid:N0}"),
            ("Quarter-frames out", qfOut.ToString("N0")),
            ("Full-frame locates", locates.ToString("N0")),
            ("Audio", audio?.Info ?? "—"),
        ]);
    }

    public string? Fault => audio?.Fault ?? midi?.Fault;
    public Offsets Offsets { set => offsets = value; }

    public void Dispose()
    {
        audio?.Dispose();
        stop = true;
        thread?.Join();
        midi?.Dispose();
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
