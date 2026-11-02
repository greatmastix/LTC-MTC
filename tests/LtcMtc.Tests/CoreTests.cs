using System.Numerics;
using Xunit;

namespace LtcMtc.Tests;

public class TimecodeTests
{
    [Theory]
    [InlineData(FrameRate.Fps24)]
    [InlineData(FrameRate.Fps25)]
    [InlineData(FrameRate.Fps2997Df)]
    [InlineData(FrameRate.Fps30)]
    public void Every_frame_of_a_day_round_trips(FrameRate r)
    {
        for (long f = 0, day = Timecode.FramesPerDay(r); f < day; f++)
            if (Timecode.FromFrames(f, r).ToFrames(r) != f) Assert.Fail($"frame {f} at {r}");
    }

    [Fact]
    public void Drop_frame_skips_the_right_labels()
    {
        var r = FrameRate.Fps2997Df;
        Assert.Equal(2589408, Timecode.FramesPerDay(r));
        Assert.Equal(new Timecode(0, 1, 0, 2), Timecode.FromFrames(1800, r));     // 00:00:59;29 → 00:01:00;02
        Assert.Equal(new Timecode(0, 10, 0, 0), Timecode.FromFrames(17982, r));   // every tenth minute keeps ;00
    }
}

public class LtcTests
{
    [Fact]
    public void Frame_bits_round_trip_with_even_zero_count()
    {
        foreach (var r in Enum.GetValues<FrameRate>())
            for (long f = 0; f < 200_000; f += 997)
            {
                var tc = Timecode.FromFrames(f, r);
                ulong bits = LtcFrame.Pack(tc, r);
                Assert.Equal(tc, LtcFrame.Unpack(bits, out bool drop));
                Assert.Equal(r.IsDrop(), drop);
                Assert.Equal(0, (80 - BitOperations.PopCount(bits) - 13) & 1);
            }
    }

    public static TheoryData<FrameRate, int, double, float, bool> Cases()
    {
        var d = new TheoryData<FrameRate, int, double, float, bool>();
        foreach (var r in Enum.GetValues<FrameRate>())
            foreach (var sr in new[] { 44100, 48000, 96000 })
                d.Add(r, sr, 1.0, 0.5f, false);
        d.Add(FrameRate.Fps25, 48000, 0.9, 0.5f, false);     // varispeed
        d.Add(FrameRate.Fps30, 48000, 1.1, 0.5f, false);
        d.Add(FrameRate.Fps25, 48000, 1.0, 0.003f, true);    // -50 dBFS, inverted, with noise
        d.Add(FrameRate.Fps25, 48000, 1.0, 0.0017f, false);  // -55 dBFS: loopback capture with the system volume turned down
        d.Add(FrameRate.Fps25, 48000, 1.0, 0.0003f, true);   // -70 dBFS
        d.Add(FrameRate.Fps2997Df, 44100, 1.0, 0.1f, true);
        return d;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Encoder_output_decodes_frame_accurately(FrameRate r, int sr, double speed, float level, bool invertAndNoise)
    {
        var start = new Timecode(10, 59, 58, 0).ToFrames(r);
        var enc = new LtcEncoder(sr) { Rate = r, Frame = start, Active = true, Level = level, Speed = speed };
        var dec = new LtcDecoder(sr);
        var got = new List<(Timecode Tc, double At)>();
        dec.FrameDecoded = (bits, at, fps) => got.Add((LtcFrame.Unpack(bits, out _), at));

        var rng = new Random(1);
        var buf = new float[480];
        for (int block = 0; block < sr * 3 / buf.Length; block++)
        {
            enc.Render(buf);
            if (invertAndNoise)
                for (int i = 0; i < buf.Length; i++) buf[i] = -buf[i] + (float)(rng.NextDouble() - 0.5) * level * 0.2f;
            dec.Process(buf);
        }

        double spf = sr / r.Fps() / speed;
        Assert.True(got.Count > r.Fps() * speed * 3 - 3, $"decoded {got.Count} frames");
        foreach (var (tc, at) in got)
        {
            long k = tc.ToFrames(r) - start;
            Assert.InRange(k, 0, 200);
            Assert.InRange(at - (k + 1) * spf, -2, 2);   // the decoded frame boundary lands within 2 samples
        }
        for (int i = 1; i < got.Count; i++) Assert.Equal(got[i - 1].Tc.ToFrames(r) + 1, got[i].Tc.ToFrames(r));
    }
}

public class MtcTests
{
    [Theory]
    [InlineData(FrameRate.Fps24)]
    [InlineData(FrameRate.Fps25)]
    [InlineData(FrameRate.Fps2997Df)]
    [InlineData(FrameRate.Fps30)]
    public void Quarter_frames_rebuild_the_timeline(FrameRate r)
    {
        var reader = new MtcReader();
        var anchors = new List<Anchor>();
        reader.Anchored = (a, _) => anchors.Add(a);
        long start = new Timecode(1, 9, 59, 20).ToFrames(r) & ~1L;   // sequences start on even frames; crosses a DF minute
        double fps = r.Fps();

        for (long q = start * 4; q < (start + 40) * 4; q++)
        {
            int msg = Mtc.QuarterFrame((int)(q & 7), Timecode.FromFrames(q >> 3 << 1, r), r);
            Assert.Equal(0xF1, msg & 0xFF);
            reader.QuarterFrame(msg >> 8, q / 4.0 / fps);
        }

        Assert.Equal(20, anchors.Count);
        Assert.Equal(0, reader.SequenceErrors);
        foreach (var a in anchors)
        {
            Assert.Equal(r, a.Rate);
            Assert.Equal(a.Frame / fps, a.Time, 9);   // the anchor's time is when that frame started
        }
    }

    [Fact]
    public void Full_frame_round_trips()
    {
        var reader = new MtcReader();
        Anchor? got = null;
        reader.Anchored = (a, locate) => { Assert.True(locate); got = a; };
        var tc = new Timecode(23, 59, 59, 29);
        reader.SysEx(Mtc.FullFrame(tc, FrameRate.Fps2997Df), 5);
        Assert.Equal(tc, Timecode.FromFrames(got!.Frame, FrameRate.Fps2997Df));
        Assert.Equal(FrameRate.Fps2997Df, got.Rate);
    }
}

public class OffsetTests
{
    [Theory]
    [InlineData("+01:00:00:00", false, 1, 0, 0, 0)]
    [InlineData("-00:00:00:05", true, 0, 0, 0, 5)]
    [InlineData("10:00:00;12", false, 10, 0, 0, 12)]
    [InlineData("-5", true, 0, 0, 0, 5)]
    [InlineData(" + 00:01:30:00 ", false, 0, 1, 30, 0)]
    public void Parses(string text, bool negative, int h, int m, int s, int f)
    {
        Assert.True(TimecodeOffset.TryParse(text, out var o));
        Assert.Equal(new TimecodeOffset(negative, new Timecode(h, m, s, f)), o);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("25:00:00:00")]
    [InlineData("00:60:00:00")]
    [InlineData("00:00:00:30")]
    [InlineData("1:2:3:4:5")]
    public void Rejects(string text) => Assert.False(TimecodeOffset.TryParse(text, out _));

    [Fact]
    public void Converts_to_frames_per_rate()
    {
        TimecodeOffset.TryParse("+01:00:00:00", out var hour);
        Assert.Equal(90000, hour.Frames(FrameRate.Fps25));
        Assert.Equal(107892, hour.Frames(FrameRate.Fps2997Df));   // one hour of 29.97 drop-frame
        TimecodeOffset.TryParse("-00:00:00:05", out var five);
        Assert.Equal(-5, five.Frames(FrameRate.Fps30));
    }
}

public class TimelineTests
{
    const FrameRate R = FrameRate.Fps25;
    static Anchor F(long frame, double time) => new(frame, time, R);

    [Fact]
    public void A_single_bad_frame_is_rejected_without_a_jump()
    {
        var t = new LtcTimeline();
        for (int i = 0; i < 10; i++) Assert.True(t.Offer(F(100 + i, i / 25.0)));
        Assert.False(t.Offer(F(5000, 10 / 25.0)));          // bit error: nonsense timecode
        Assert.True(t.Offer(F(111, 11 / 25.0)));            // back on track
        Assert.Equal((0, 1), (t.Jumps, t.Rejected));
        Assert.Equal(111, t.Current!.Frame);
    }

    [Fact]
    public void A_dropped_frame_still_continues()
    {
        var t = new LtcTimeline();
        t.Offer(F(100, 0));
        Assert.True(t.Offer(F(102, 2 / 25.0)));
        Assert.Equal((0, 0), (t.Jumps, t.Rejected));
    }

    [Fact]
    public void A_real_jump_is_taken_once_confirmed()
    {
        var t = new LtcTimeline();
        t.Offer(F(100, 0));
        Assert.False(t.Offer(F(9000, 1 / 25.0)));
        Assert.True(t.Offer(F(9001, 2 / 25.0)));
        Assert.Equal((1, 1), (t.Jumps, t.Rejected));
        Assert.Equal(9001, t.Current!.Frame);
    }
}

public class MmcTests
{
    [Fact]
    public void Commands_and_locate_match_the_spec()
    {
        Assert.Equal(new byte[] { 0xF0, 0x7F, 0x7F, 0x06, 0x03, 0xF7 }, Mmc.Command(Mmc.AllDevices, Mmc.DeferredPlay));
        Assert.Equal(new byte[] { 0xF0, 0x7F, 0x00, 0x06, 0x01, 0xF7 }, Mmc.Command(0, Mmc.Stop));
        // 01:02:03:04 at 25 fps: hours byte carries rate code 1 in bits 5-6 → 0x21.
        Assert.Equal(new byte[] { 0xF0, 0x7F, 0x7F, 0x06, 0x44, 0x06, 0x01, 0x21, 0x02, 0x03, 0x04, 0x00, 0xF7 },
            Mmc.Locate(Mmc.AllDevices, new Timecode(1, 2, 3, 4), FrameRate.Fps25));
    }
}

public class MmcLocateOptionTests
{
    [Fact]
    public void Hours_can_omit_the_rate_bits()
    {
        var m = Mmc.Locate(Mmc.AllDevices, new Timecode(23, 59, 59, 21), FrameRate.Fps25, rateBits: false);
        Assert.Equal(23, m[7]);
        Assert.Equal(0x20 | 23, Mmc.Locate(Mmc.AllDevices, new Timecode(23, 59, 59, 21), FrameRate.Fps25)[7]);
    }
}
