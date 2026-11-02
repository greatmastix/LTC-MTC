using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LtcMtc;

/// <summary>Fill a mono block; <paramref name="time"/> is when its first sample leaves the speaker (QPC seconds).</summary>
public delegate void RenderFn(Span<float> mono, double time);
/// <summary>Consume a mono block; <paramref name="time"/> is when its first sample entered the converter (QPC seconds).</summary>
public delegate void CaptureFn(ReadOnlySpan<float> mono, double time);

public abstract class AudioStream : IDisposable
{
    public int SampleRate { get; protected set; }
    public string Info { get; protected set; } = "";
    public abstract void Start();
    public abstract void Dispose();
}

/// <summary>Device list and factory. Keys are "wasapi:{endpoint id}" or "asio:{driver name}".</summary>
public static class Audio
{
    static readonly MMDeviceEnumerator Mm = new();

    public static List<(string Key, string Name)> Devices(bool output)
    {
        var list = new List<(string, string)>();
        foreach (var d in Mm.EnumerateAudioEndPoints(output ? DataFlow.Render : DataFlow.Capture, DeviceState.Active))
            list.Add(("wasapi:" + d.ID, "WASAPI · " + d.FriendlyName));
        try { foreach (var n in AsioDevice.GetDriverNames()) list.Add(("asio:" + n, "ASIO · " + n)); } catch { /* no ASIO */ }
        return list;
    }

    public static int Channels(string key, bool output)
    {
        try
        {
            if (key.StartsWith("wasapi:")) { using var c = Mm.GetDevice(key[7..]).CreateAudioClient(); return c.MixFormat.Channels; }
            using var d = AsioDevice.Open(key[5..]);
            return output ? d.Capabilities.NbOutputChannels : d.Capabilities.NbInputChannels;
        }
        catch { return 2; }
    }

    /// <param name="channel">0-based output channel, or -1 for all channels.</param>
    public static AudioStream Output(string key, int channel, int bufferMs, RenderFn render) => key.StartsWith("asio:")
        ? new AsioOutput(key[5..], channel, render) : new WasapiOutput(Mm.GetDevice(key[7..]), channel, bufferMs, render);

    public static AudioStream Input(string key, int channel, int bufferMs, CaptureFn capture) => key.StartsWith("asio:")
        ? new AsioInput(key[5..], channel, capture) : new WasapiInput(Mm.GetDevice(key[7..]), channel, bufferMs, capture);

    internal static float[] Grow(ref float[] buf, int n) => buf.Length >= n ? buf : buf = new float[n];
}

/// <summary>Callback wall-clock time, smoothed against the sample count so scheduling jitter does not reach the PLL.</summary>
sealed class SmoothClock
{
    double next = double.NaN;

    public double Block(int frames, int sampleRate)
    {
        double now = Clock.Now, t = double.IsNaN(next) || Math.Abs(now - next) > 0.02 ? now : next + (now - next) * 0.02;
        next = t + (double)frames / sampleRate;
        return t;
    }
}

sealed class WasapiOutput : AudioStream, IWaveProvider
{
    readonly WasapiPlayer player;
    readonly RenderFn render;
    readonly SmoothClock clock = new();
    readonly int channels, channel;
    readonly double latency;
    float[] mono = new float[4096];

    public WaveFormat WaveFormat { get; }

    public WasapiOutput(MMDevice device, int channel, int bufferMs, RenderFn render)
    {
        this.render = render;
        this.channel = channel;
        player = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithEventSync().WithLowLatency()
            .WithLatency(bufferMs).WithMmcssThreadPriority("Pro Audio").Build();
        var mix = player.DeviceMixFormat;
        (SampleRate, channels) = (mix.SampleRate, mix.Channels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, channels);
        player.Init(this);
        latency = player.LatencyMilliseconds / 1000.0;
        Info = $"WASAPI · {SampleRate / 1000.0:0.#} kHz · {player.LatencyMilliseconds} ms{(player.LowLatencyActive ? " · low-latency" : "")}";
    }

    public override void Start() => player.Play();

    public int Read(Span<byte> buffer)
    {
        var dst = MemoryMarshal.Cast<byte, float>(buffer);
        int n = dst.Length / channels;
        var m = Audio.Grow(ref mono, n).AsSpan(0, n);
        render(m, clock.Block(n, SampleRate) + latency);
        if (channel >= 0) dst.Clear();
        for (int i = 0; i < n; i++)
            if (channel < 0) dst.Slice(i * channels, channels).Fill(m[i]); else dst[i * channels + channel] = m[i];
        return buffer.Length;
    }

    public override void Dispose() { player.Stop(); player.Dispose(); }
}

sealed class WasapiInput : AudioStream
{
    readonly WasapiRecorder recorder;
    float[] mono = new float[4096];

    public WasapiInput(MMDevice device, int channel, int bufferMs, CaptureFn capture)
    {
        recorder = new WasapiRecorderBuilder().WithDevice(device).WithSharedMode().WithEventSync().WithLowLatency()
            .WithBufferLength(bufferMs).WithMmcssThreadPriority("Pro Audio").Build();
        var f = recorder.WaveFormat;
        if (f.BitsPerSample != 32) throw new NotSupportedException($"Expected a 32-bit float capture format, got {f}.");
        int channels = f.Channels;
        SampleRate = f.SampleRate;
        Info = $"WASAPI · {SampleRate / 1000.0:0.#} kHz · {recorder.LatencyMilliseconds} ms{(recorder.LowLatencyActive ? " · low-latency" : "")}";
        recorder.DataAvailable += (buffer, flags, _, qpc) =>
        {
            var src = MemoryMarshal.Cast<byte, float>(buffer);
            int n = src.Length / channels;
            var m = Audio.Grow(ref mono, n).AsSpan(0, n);
            if ((flags & AudioClientBufferFlags.Silent) != 0) m.Clear();
            else for (int i = 0; i < n; i++) m[i] = src[i * channels + channel];
            capture(m, qpc * 1e-7);   // QPC in 100 ns units: the same clock as Clock.Now
        };
    }

    public override void Start() => recorder.StartRecording();

    public override void Dispose() { recorder.StopRecording(); recorder.Dispose(); }
}

sealed class AsioOutput : AudioStream
{
    readonly AsioDevice device;
    readonly RenderFn render;
    readonly SmoothClock clock = new();
    readonly double latency;

    public AsioOutput(string driver, int channel, RenderFn render)
    {
        this.render = render;
        device = AsioDevice.Open(driver);
        try
        {
            var outs = channel < 0 ? device.Capabilities.AllOutputChannels : [channel];
            device.InitDuplex(new AsioDuplexOptions { InputChannels = [], OutputChannels = outs, Processor = Process });
            SampleRate = device.CurrentSampleRate;
            latency = (double)device.OutputLatencySamples / SampleRate;
            Info = $"ASIO · {SampleRate / 1000.0:0.#} kHz · {device.FramesPerBuffer} samples · {latency * 1000:0.#} ms";
        }
        catch { device.Dispose(); throw; }
    }

    public override void Start() => device.Start();

    void Process(in AsioProcessBuffers b)
    {
        var first = b.GetOutput(0);
        render(first, clock.Block(b.Frames, b.SampleRate) + latency);
        for (int c = 1; c < b.OutputChannelCount; c++) first.CopyTo(b.GetOutput(c));
    }

    public override void Dispose() { device.Stop(); device.Dispose(); }
}

sealed class AsioInput : AudioStream
{
    readonly AsioDevice device;

    public AsioInput(string driver, int channel, CaptureFn capture)
    {
        device = AsioDevice.Open(driver);
        try
        {
            device.InitRecording(new AsioRecordingOptions { InputChannels = [channel] });
            SampleRate = device.CurrentSampleRate;
            double latency = (double)device.InputLatencySamples / SampleRate;
            Info = $"ASIO · {SampleRate / 1000.0:0.#} kHz · {device.FramesPerBuffer} samples · {latency * 1000:0.#} ms";
            var clock = new SmoothClock();
            // The callback fires once the block is complete: its first sample arrived one block (plus latency) ago.
            device.AudioCaptured += (_, e) =>
                capture(e.GetChannel(0), clock.Block(e.Frames, e.SampleRate) - (double)e.Frames / e.SampleRate - latency);
        }
        catch { device.Dispose(); throw; }
    }

    public override void Start() => device.Start();

    public override void Dispose() { device.Stop(); device.Dispose(); }
}
