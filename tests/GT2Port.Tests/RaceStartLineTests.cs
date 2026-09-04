using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// Which frame the room is held at.
///
/// The barrier used to be counted in FrameBegins, which is hooked on a method
/// that runs exactly once per race. A counter there cannot reach three, so
/// asking it to hold at three held at nothing: two machines raced with no
/// barrier and the only sign was a log with no hold line in it. The count now
/// comes from the per-frame hook, and what it means is written down here.
/// </summary>
public class RaceStartLineTests
{
    [Theory]
    [InlineData(0, 3, false)]
    [InlineData(2, 3, false)]
    [InlineData(3, 3, true)]
    [InlineData(4, 3, false)]
    [InlineData(0, 0, true)]
    public void It_holds_on_the_frame_it_was_asked_for(int framesSoFar, int holdAt, bool holds)
    {
        Assert.Equal(holds,
            RaceStartLine.WouldHold(holdsHere: true, alreadyHeld: false, framesSoFar, holdAt));
    }

    /// <summary>
    /// Once is the whole point. A barrier that fired again mid-race would stop
    /// the race dead waiting for a room that is already racing.
    /// </summary>
    [Fact]
    public void And_never_twice_in_one_race()
    {
        Assert.False(
            RaceStartLine.WouldHold(holdsHere: true, alreadyHeld: true, framesSoFar: 3, holdAt: 3));
    }

    /// <summary>
    /// GT2_HOLD_AT_OVERLAY and GT2_START_AT_PHASE both put the barrier
    /// somewhere else, and two barriers would be two handshakes where the
    /// session expects one.
    /// </summary>
    [Fact]
    public void And_not_at_all_when_something_else_is_holding()
    {
        Assert.False(
            RaceStartLine.WouldHold(holdsHere: false, alreadyHeld: false, framesSoFar: 3, holdAt: 3));
    }

    /// <summary>
    /// Three, measured: with the barrier at frame zero the frame straight
    /// after it cost 304ms on one machine and 487ms on the other, and that
    /// 183ms went into the start.
    /// </summary>
    [Fact]
    public void The_frame_it_holds_at_is_three_unless_asked_otherwise()
    {
        Assert.Equal(3, RaceStartLine.HoldsAtFrame);
    }

    /// <summary>
    /// A second race has to be able to hold again, and the counter the barrier
    /// keys on is the one that used to be left alone by Forget - so the second
    /// race would have started at whatever number the first one ended on and
    /// never held.
    /// </summary>
    [Fact]
    public void Forgetting_a_race_resets_the_count_the_barrier_keys_on()
    {
        RaceStartLine.Forget();

        Assert.Equal(0, RaceStartLine.FramesSoFar);
        Assert.False(RaceStartLine.Held);
    }
}
