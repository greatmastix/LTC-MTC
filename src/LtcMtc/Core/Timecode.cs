using System.Diagnostics;

namespace LtcMtc;

/// <summary>Frame rates as MTC encodes them (the enum value is the MTC rate code).</summary>
public enum FrameRate { Fps24 = 0, Fps25 = 1, Fps2997Df = 2, Fps30 = 3 }

public static class FrameRates
{
    public static int Nominal(this FrameRate r) => r switch { FrameRate.Fps24 => 24, FrameRate.Fps25 => 25, _ => 30 };
    public static double Fps(this FrameRate r) => r == FrameRate.Fps2997Df ? 30000 / 1001.0 : r.Nominal();
    public static bool IsDrop(this FrameRate r) => r == FrameRate.Fps2997Df;
    public static string Label(this FrameRate r) => r switch
    {
        FrameRate.Fps24 => "24 fps", FrameRate.Fps25 => "25 fps", FrameRate.Fps2997Df => "29.97 DF", _ => "30 fps",
    };

    /// <summary>The nearest rate to a measured frames-per-second value; the DF flag decides 29.97.</summary>
    public static FrameRate FromMeasured(double fps, bool drop) =>
        drop ? FrameRate.Fps2997Df : fps < 24.5 ? FrameRate.Fps24 : fps < 27.5 ? FrameRate.Fps25 : FrameRate.Fps30;
}

public readonly record struct Timecode(int H, int M, int S, int F)
{
    public override string ToString() => $"{H:00}:{M:00}:{S:00}:{F:00}";

    /// <summary>"HH:MM:SS:FF" (or shorter, like "SS:FF"); ';' or '.' also separate frames.</summary>
    public static bool TryParse(string text, out Timecode tc)
    {
        tc = default;
        if (text.TrimStart().StartsWith('-') || !TimecodeOffset.TryParse(text, out var o)) return false;
        tc = o.Tc;
        return true;
    }

    public static long FramesPerDay(FrameRate r) => new Timecode(24, 0, 0, 0).ToFrames(r);

    /// <summary>Real frames since 00:00:00:00 (drop-frame labels skipped).</summary>
    public long ToFrames(FrameRate r)
    {
        long n = r.Nominal(), mins = H * 60L + M;
        long f = (mins * 60 + S) * n + F;
        return r.IsDrop() ? f - 2 * (mins - mins / 10) : f;
    }

    public static Timecode FromFrames(long f, FrameRate r)
    {
        long day = FramesPerDay(r);
        f = (f % day + day) % day;
        if (r.IsDrop())
        {
            long d = f / 17982, m = f % 17982;
            f += 18 * d + (m < 2 ? 0 : 2 * ((m - 2) / 1798));
        }
        int n = r.Nominal();
        return new((int)(f / (3600L * n)), (int)(f / (60L * n) % 60), (int)(f / n % 60), (int)(f % n));
    }
}

/// <summary>One time base for everything: QPC seconds (the same clock WASAPI stamps capture packets with).</summary>
public static class Clock
{
    static readonly double Tick = 1.0 / Stopwatch.Frequency;
    public static double Now => Stopwatch.GetTimestamp() * Tick;
}

/// <summary>"Frame <see cref="Frame"/> started at <see cref="Time"/>": the hand-off between input and output threads.</summary>
public sealed record Anchor(long Frame, double Time, FrameRate Rate)
{
    /// <summary>Position in frames (fractional) at a given time.</summary>
    public double FrameAt(double time) => Frame + (time - Time) * Rate.Fps();
    public double Seconds => Frame / Rate.Fps();
}

/// <summary>A signed timecode offset such as "+01:00:00:00" or "-00:00:00:05", added to the converted timecode.</summary>
public sealed record TimecodeOffset(bool Negative, Timecode Tc)
{
    public static readonly TimecodeOffset Zero = new(false, default);

    /// <summary>The offset in real frames at <paramref name="r"/> (labels, so 01:00:00:00 is one hour of 29.97 DF too).</summary>
    public long Frames(FrameRate r) => (Negative ? -1 : 1) * Tc.ToFrames(r);

    public override string ToString() => (Negative ? "-" : "+") + Tc;

    /// <summary>Accepts "[+|-]HH:MM:SS:FF" (';' or '.' also separate frames), or a plain frame count like "-5".</summary>
    public static bool TryParse(string text, out TimecodeOffset offset)
    {
        offset = Zero;
        var s = text.Trim();
        bool neg = s.StartsWith('-');
        if (s.StartsWith('-') || s.StartsWith('+')) s = s[1..].Trim();
        var parts = s.Split(':', ';', '.');
        if (parts.Length is < 1 or > 4 || !parts.All(p => p.Length is > 0 and <= 2 && p.All(char.IsAsciiDigit))) return false;
        var v = new int[4];
        for (int i = 0; i < parts.Length; i++) v[4 - parts.Length + i] = int.Parse(parts[i]);
        if (v[0] > 23 || v[1] > 59 || v[2] > 59 || v[3] > 29) return false;
        offset = new(neg, new Timecode(v[0], v[1], v[2], v[3]));
        return true;
    }
}
