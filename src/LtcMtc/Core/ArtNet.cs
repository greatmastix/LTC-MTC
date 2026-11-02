namespace LtcMtc;

/// <summary>Art-Net timecode (ArtTimeCode, OpCode 0x9700): one UDP packet per frame on port 6454.</summary>
public static class ArtNet
{
    public const int Port = 6454;

    /// <summary>"Art-Net\0", OpCode 0x9700 (little-endian), ProtVer 14, two filler bytes, frames, seconds, minutes, hours,
    /// type. The type codes (0 = 24, 1 = 25, 2 = 29.97 DF, 3 = 30) are the same as MTC's and <see cref="FrameRate"/>'s.</summary>
    public static byte[] TimeCode(Timecode tc, FrameRate r) =>
        [(byte)'A', (byte)'r', (byte)'t', (byte)'-', (byte)'N', (byte)'e', (byte)'t', 0, 0x00, 0x97, 0, 14, 0, 0,
         (byte)tc.F, (byte)tc.S, (byte)tc.M, (byte)tc.H, (byte)r];

    public static bool TryParseTimeCode(ReadOnlySpan<byte> p, out Timecode tc, out FrameRate r)
    {
        (tc, r) = (default, default);
        if (p.Length < 19 || !p[..8].SequenceEqual("Art-Net\0"u8) || p[8] != 0x00 || p[9] != 0x97) return false;
        r = (FrameRate)(p[18] & 3);
        tc = new Timecode(p[17], p[16], p[15], p[14]);
        return tc.H < 24 && tc.M < 60 && tc.S < 60 && tc.F < r.Nominal();
    }
}
