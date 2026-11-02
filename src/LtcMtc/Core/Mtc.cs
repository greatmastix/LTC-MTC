namespace LtcMtc;

/// <summary>MIDI Time Code: quarter-frame messages (F1 0n dddd) and the full-frame SysEx (F0 7F 7F 01 01 hh mm ss ff F7).</summary>
public static class Mtc
{
    /// <summary>A quarter-frame as a packed short message (status in the low byte, ready for midiOutShortMsg).</summary>
    public static int QuarterFrame(int piece, Timecode tc, FrameRate r)
    {
        int v = piece switch
        {
            0 => tc.F & 15, 1 => tc.F >> 4, 2 => tc.S & 15, 3 => tc.S >> 4,
            4 => tc.M & 15, 5 => tc.M >> 4, 6 => tc.H & 15, _ => (tc.H >> 4) | ((int)r << 1),
        };
        return 0xF1 | (piece << 4 | v) << 8;
    }

    public static byte[] FullFrame(Timecode tc, FrameRate r) =>
        [0xF0, 0x7F, 0x7F, 0x01, 0x01, (byte)(tc.H | (int)r << 5), (byte)tc.M, (byte)tc.S, (byte)tc.F, 0xF7];
}

/// <summary>Reassembles MTC into <see cref="Anchor"/>s. Feed it from the MIDI callback thread.</summary>
public sealed class MtcReader
{
    /// <summary>(frame start anchor, true for a full-frame locate).</summary>
    public Action<Anchor, bool>? Anchored;
    public long QuarterFrames, SequenceErrors;
    public double LastMessage = double.NegativeInfinity;

    readonly int[] p = new int[8];
    int expect = -1, have;

    public void QuarterFrame(int data, double time)
    {
        int piece = data >> 4;
        QuarterFrames++;
        LastMessage = time;
        if (expect >= 0 && piece != expect) { SequenceErrors++; have = 0; }
        expect = (piece + 1) & 7;
        p[piece] = data & 15;
        have |= 1 << piece;
        if (piece != 7 || have != 0xFF) return;
        have = 0;
        var r = (FrameRate)(p[7] >> 1 & 3);
        var tc = new Timecode(p[6] | (p[7] & 1) << 4, p[4] | p[5] << 4, p[2] | p[3] << 4, p[0] | p[1] << 4);
        // The eight pieces describe the frame during which piece 0 went out, 1.75 frames before piece 7.
        Anchored?.Invoke(new Anchor(tc.ToFrames(r), time - 1.75 / r.Fps(), r), false);
    }

    public void SysEx(ReadOnlySpan<byte> m, double time)
    {
        if (m.Length < 10 || m[1] != 0x7F || m[3] != 0x01 || m[4] != 0x01) return;
        var r = (FrameRate)(m[5] >> 5 & 3);
        var tc = new Timecode(m[5] & 31, m[6], m[7], m[8]);
        LastMessage = time;
        expect = -1;
        have = 0;
        Anchored?.Invoke(new Anchor(tc.ToFrames(r), time, r), true);
    }
}

/// <summary>MIDI Machine Control: transport commands as universal real-time SysEx (F0 7F dev 06 ...).</summary>
public static class Mmc
{
    public const byte AllDevices = 0x7F, Stop = 0x01, Play = 0x02, DeferredPlay = 0x03;

    public static byte[] Command(byte device, byte command) => [0xF0, 0x7F, device, 0x06, command, 0xF7];

    /// <summary>LOCATE [TARGET]: F0 7F dev 06 44 06 01 hr mn sc fr sf F7. The spec puts the rate code in bits 5-6 of hr,
    /// as in MTC; some receivers read hr as plain hours, so <paramref name="rateBits"/> can leave it out.</summary>
    public static byte[] Locate(byte device, Timecode tc, FrameRate r, bool rateBits = true) =>
        [0xF0, 0x7F, device, 0x06, 0x44, 0x06, 0x01, (byte)(tc.H | (rateBits ? (int)r << 5 : 0)), (byte)tc.M, (byte)tc.S, (byte)tc.F, 0x00, 0xF7];
}
