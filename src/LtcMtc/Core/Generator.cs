namespace LtcMtc;

/// <summary>
/// The internal timecode clock: play, pause, stop (back to the start time) and locate. Its state is one immutable
/// record, swapped atomically, so the output threads never see a "playing" flag that disagrees with the position.
/// </summary>
public sealed class GeneratorClock(FrameRate rate, long start)
{
    public sealed record State(Anchor Anchor, bool Playing);

    volatile State state = new(new Anchor(start, Clock.Now, rate), false);

    public FrameRate Rate => rate;
    public long Start { get; set; } = start;
    public State Current => state;

    public long FrameAt(double now) => state is { Playing: true } s ? (long)Math.Floor(s.Anchor.FrameAt(now)) : state.Anchor.Frame;

    public void Play(double now)
    {
        if (!state.Playing) state = new(new Anchor(state.Anchor.Frame, now, rate), true);
    }

    public void Pause(double now)
    {
        if (state.Playing) state = new(new Anchor(FrameAt(now), now, rate), false);
    }

    /// <summary>Stops and goes back to the start time.</summary>
    public void Stop(double now) => state = new(new Anchor(Start, now, rate), false);

    /// <summary>Jumps to a frame, playing or not.</summary>
    public void Locate(long frame, double now) => state = new(new Anchor(frame, now, rate), state.Playing);
}
