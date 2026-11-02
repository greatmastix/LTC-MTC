using System.Numerics;

namespace LtcMtc;

/// <summary>SMPTE 12M linear timecode: 80 bits per frame, biphase-mark coded, LSB first.</summary>
public static class LtcFrame
{
    /// <summary>Bits 64-79, the sync word 0011 1111 1111 1101, as a 16-bit value with bit 64 as the LSB.</summary>
    public const ushort Sync = 0xBFFC;

    public static ulong Pack(Timecode tc, FrameRate r)
    {
        static ulong Bcd(int v, int pos) => (ulong)(uint)(v % 10) << pos | (ulong)(uint)(v / 10) << (pos + 8);
        ulong b = Bcd(tc.F, 0) | Bcd(tc.S, 16) | Bcd(tc.M, 32) | Bcd(tc.H, 48) | (r.IsDrop() ? 1UL << 10 : 0);
        // Polarity correction: an even number of zeros per 80-bit word (the sync word has 13 ones).
        if (((BitOperations.PopCount(b) + 13) & 1) == 1) b |= 1UL << (r == FrameRate.Fps25 ? 59 : 27);
        return b;
    }

    public static Timecode Unpack(ulong b, out bool drop)
    {
        int d(int pos, int bits) => (int)(b >> pos) & ((1 << bits) - 1);
        drop = d(10, 1) == 1;
        return new(d(56, 2) * 10 + d(48, 4), d(40, 3) * 10 + d(32, 4), d(24, 3) * 10 + d(16, 4), d(8, 2) * 10 + d(0, 4));
    }
}

/// <summary>
/// Renders LTC into float samples. At every frame boundary it calls <see cref="FrameStart"/>, which decides which frame
/// comes next, its rate, and a playback <see cref="Speed"/> (the PLL's knob).
/// </summary>
public sealed class LtcEncoder(int sampleRate)
{
    public FrameRate Rate = FrameRate.Fps25;
    public long Frame;
    public double Speed = 1, Pos;     // Pos: samples into the current frame
    public bool Active;
    public float Level = 0.25f;       // peak amplitude
    /// <summary>Called on the audio thread at each frame boundary with the boundary's index within the block.</summary>
    public Action<int>? FrameStart;

    ulong bits;
    int half = -1;
    float state = 1, y;
    readonly float smooth = 1 - MathF.Exp(-2 * MathF.PI * 14000 / sampleRate); // ~25 µs rise time, as SMPTE asks

    public void Render(Span<float> buf)
    {
        double spf = sampleRate / Rate.Fps();
        for (int i = 0; i < buf.Length; i++)
        {
            if (half < 0) { FrameStart?.Invoke(i); spf = sampleRate / Rate.Fps(); bits = LtcFrame.Pack(Timecode.FromFrames(Frame, Rate), Rate); }
            int h = Math.Min((int)(Pos * 160 / spf), 159);
            if (h != half)
            {
                int bit = h >> 1;
                // Biphase mark: a transition at every bit start, and mid-bit for a one.
                if ((h & 1) == 0 || (bit < 64 ? (bits >> bit) & 1 : (ulong)(LtcFrame.Sync >> (bit - 64)) & 1) == 1) state = -state;
                half = h;
            }
            buf[i] = y += ((Active ? state * Level : 0) - y) * smooth;

            Pos += Speed;
            if (Pos >= spf) { Pos -= spf; Frame++; half = -1; }
        }
    }
}

/// <summary>
/// Decodes LTC from float samples: hysteresis zero-crossings with sub-sample interpolation, an adaptive bit period,
/// and an 80-bit shift register that fires <see cref="FrameDecoded"/> on each sync word.
/// </summary>
public sealed class LtcDecoder(int sampleRate)
{
    /// <summary>(frame bits 0-63, sample position where the next frame starts, measured fps).</summary>
    public Action<ulong, double, double>? FrameDecoded;
    public float Envelope { get; private set; }

    readonly float decay = MathF.Exp(-1f / (0.05f * sampleRate));
    double bitLen = sampleRate / 2160.0, last;   // starting guess sits between 24 fps (1920 bit/s) and 30 fps (2400)
    long n;
    float prev;
    bool high, halfSeen;
    UInt128 reg;

    public long SamplesProcessed => n;

    public void Process(ReadOnlySpan<float> buf)
    {
        for (int i = 0; i < buf.Length; i++, n++)
        {
            float x = buf[i], a = MathF.Abs(x);
            Envelope = a > Envelope ? a : Envelope * decay;
            float th = MathF.Max(Envelope * 0.2f, 1e-5f);    // relative threshold; the floor only rejects near-digital-silence (-100 dBFS)
            bool h = high ? x > -th : x > th;
            if (h != high)
            {
                float level = h ? th : -th;
                double t = n - 1 + Math.Clamp((prev - level) / (prev - x), 0, 1);
                Edge(t - last, t);
                last = t;
                high = h;
            }
            prev = x;
        }
    }

    void Edge(double d, double t)
    {
        if (d > bitLen * 2 || d < bitLen * 0.25) { halfSeen = false; return; }   // noise or a gap: wait for clean bits
        if (d > bitLen * 0.75) { Track(d); halfSeen = false; Push(0, t); }
        else
        {
            Track(2 * d);
            if (halfSeen) Push(1, t);
            halfSeen = !halfSeen;
        }
    }

    void Track(double full) => bitLen = Math.Clamp(bitLen + (full - bitLen) * 0.1, sampleRate / 4800.0, sampleRate / 960.0);

    void Push(int bit, double t)
    {
        reg = (reg >> 1) | ((UInt128)(uint)bit << 79);
        if ((ushort)(reg >> 64) == LtcFrame.Sync) FrameDecoded?.Invoke((ulong)reg, t, sampleRate / (80 * bitLen));
    }
}

/// <summary>
/// Turns decoded LTC frames into a timeline that one bad frame can't move. A frame is accepted when it continues the
/// timeline: same rate, a little later, and arriving when the timeline says it should (so a dropped frame is fine). A
/// frame that doesn't is held back, and only a following frame that continues from it confirms a real jump.
/// </summary>
public sealed class LtcTimeline
{
    public Anchor? Current { get; private set; }
    public long Jumps, Rejected;
    Anchor? pending;

    public bool Offer(Anchor a)
    {
        if (Current == null || Continues(Current, a)) { (Current, pending) = (a, null); return true; }
        if (pending != null && Continues(pending, a)) { (Current, pending) = (a, null); Jumps++; return true; }
        pending = a;
        Rejected++;
        return false;
    }

    static bool Continues(Anchor from, Anchor a)
    {
        long n = a.Frame - from.Frame;
        double fps = a.Rate.Fps();
        return a.Rate == from.Rate && n is > 0 and <= 5 && Math.Abs(a.Time - from.Time - n / fps) < 0.25 / fps;
    }
}
