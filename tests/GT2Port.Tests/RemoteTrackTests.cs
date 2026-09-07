using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// Where a remote car is drawn, given what has arrived about it.
///
/// The car used to be put wherever the newest place said, the instant it
/// arrived - a step per datagram, of whatever size the gap happened to be,
/// and nothing at all while one was missing. This is what replaces that, and
/// what it has to get right is small and easy to get subtly wrong: a car that
/// crosses the wrap in its heading, a connection whose arrivals are ragged,
/// and a buffer that runs dry.
/// </summary>
public class RemoteTrackTests
{
    static readonly DateTime Noon = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    static RemoteCars.Pose At(int x, short facing = 0) =>
        new(new RemoteCars.Place(x, 0, 0), 0, facing, 0, new RemoteCars.Wheels(0, 0, 0, 0));

    /// <summary>
    /// A steady sender: one place every <paramref name="every"/> milliseconds
    /// of its own clock, arriving <paramref name="late"/> milliseconds later
    /// than the one before by this machine's.
    /// </summary>
    static RemoteTrack Steady(int places, int every = 33, Func<int, int>? late = null,
                              Func<int, int>? x = null)
    {
        var track = new RemoteTrack();
        var arrived = Noon;

        for (int i = 0; i < places; i++)
        {
            if (i > 0) arrived = arrived.AddMilliseconds(late?.Invoke(i) ?? every);
            track.Heard(i == 0 ? 0 : every, At(x?.Invoke(i) ?? i * 100), arrived);
        }

        return track;
    }

    [Fact]
    public void Nothing_heard_is_nowhere_to_draw()
    {
        Assert.Null(new RemoteTrack().At(Noon));
    }

    /// <summary>
    /// The point of the whole thing: between two places, the car is somewhere
    /// between them, and not at either.
    /// </summary>
    [Fact]
    public void A_car_is_drawn_between_the_places_it_was_at()
    {
        var track = new RemoteTrack();
        track.Heard(0, At(0), Noon);
        track.Heard(100, At(1000), Noon.AddMilliseconds(100));

        // A hundred-millisecond interval and no jitter puts the car exactly on
        // the older of the two the moment the newer lands - which is the point
        // of the delay, and the start of the arc rather than a spot on it.
        Assert.Equal(0, track.At(Noon.AddMilliseconds(100))!.Place.X);

        // Then this machine's own clock carries it along that arc.
        var drawn = track.At(Noon.AddMilliseconds(140));

        Assert.NotNull(drawn);
        Assert.InRange(drawn!.Place.X, 1, 999);
    }

    /// <summary>
    /// A car that keeps arriving keeps being drawn further along. Not a
    /// tautology: the point being drawn moves with this machine's clock, so a
    /// mistake in that arithmetic shows up as a car that stops or runs away.
    /// </summary>
    [Fact]
    public void And_goes_forwards_as_the_places_do()
    {
        var track = Steady(20);

        int before = track.At(Noon.AddMilliseconds(600))!.Place.X;
        int after = track.At(Noon.AddMilliseconds(630))!.Place.X;

        Assert.True(after > before, $"drawn at {before} then {after} - a car going backwards");
    }

    /// <summary>
    /// The car is drawn behind the newest place on purpose. Not behind it at
    /// all would leave nothing to interpolate towards, which is the old
    /// behaviour.
    /// </summary>
    [Fact]
    public void A_car_is_drawn_behind_where_it_was_last_heard_to_be()
    {
        var track = Steady(20);

        var drawn = track.At(Noon.AddMilliseconds(19 * 33));

        Assert.NotNull(drawn);
        Assert.True(drawn!.Place.X < 19 * 100,
            "drawn at or past the newest place leaves nothing to draw towards");
    }

    /// <summary>
    /// A clean link is drawn at the floor. Any more would be latency added for
    /// nothing.
    /// </summary>
    [Fact]
    public void A_steady_connection_is_drawn_at_the_shortest_delay()
    {
        var track = Steady(40);

        Assert.Equal(RemoteTrack.Least, track.Delay);
    }

    /// <summary>
    /// And a ragged one is drawn further back, which is the adaptive part: the
    /// buffer has to be deep enough to cover the late arrivals or it runs dry
    /// and the car stops.
    /// </summary>
    [Fact]
    public void A_ragged_connection_is_drawn_further_back()
    {
        // Sent every 33ms, arriving alternately early and very late.
        var track = Steady(60, late: i => i % 2 == 0 ? 3 : 63);

        Assert.True(track.Delay > RemoteTrack.Least,
            $"a jittery link settled on {track.Delay.TotalMilliseconds:F0}ms, the floor");
        Assert.True(track.Delay <= RemoteTrack.Most);
    }

    /// <summary>
    /// Ragged enough and the delay stops at the ceiling rather than following
    /// the measurement wherever it goes.
    ///
    /// Driven by lateness the buffer could nearly cover: past the ceiling an
    /// arrival is an outage rather than a cadence and is not measured at all -
    /// see <see cref="Nor_does_a_gap_in_the_arrivals"/> - so a link that could
    /// once be pushed here with nine-hundred-millisecond arrivals is now
    /// pushed here with two-hundred-and-forty-millisecond ones.
    /// </summary>
    [Fact]
    public void And_never_further_than_the_ceiling()
    {
        var track = Steady(60, late: i => i % 2 == 0 ? 1 : 240);

        Assert.Equal(RemoteTrack.Most, track.Delay);
    }

    /// <summary>
    /// A heading crossing the wrap goes the short way. Read straight, a car
    /// stepping from just under a whole turn to just over nothing spins all
    /// the way back through every heading it does not have - which on screen
    /// is the car snapping round twice in one corner.
    /// </summary>
    [Fact]
    public void A_heading_that_wraps_goes_the_short_way()
    {
        var track = new RemoteTrack();
        track.Heard(0, At(0, facing: 4090), Noon);
        track.Heard(100, At(0, facing: 6), Noon.AddMilliseconds(100));

        var drawn = track.At(Noon.AddMilliseconds(100));

        Assert.NotNull(drawn);

        // Twelve units separate them the short way. Anything in between is on
        // that arc; the long way round would be hundreds off.
        int from = 4090;
        int distance = ((drawn!.AroundY - from) % RemoteCars.WholeTurn
                        + RemoteCars.WholeTurn) % RemoteCars.WholeTurn;
        Assert.InRange(distance, 0, 12);
    }

    /// <summary>
    /// When the places stop, the car finishes the last move it knew about and
    /// then holds. It does not run off on its own - that is dead reckoning and
    /// is not this - and it does not snap back either.
    /// </summary>
    [Fact]
    public void When_the_places_stop_the_car_settles_at_the_last_one()
    {
        var track = Steady(10);

        // Frame by frame, the way the game asks - a single call much later
        // would only ever be the first one, and the draining happens between
        // them.
        RemoteCars.Pose? far = null;
        for (int frame = 0; frame < 200; frame++)
            far = track.At(Noon.AddMilliseconds(9 * 33 + frame * 16));

        Assert.NotNull(far);
        Assert.Equal(9 * 100, far!.Place.X);
    }

    /// <summary>
    /// And the history stays short. A race is minutes long and a car that kept
    /// every place it was ever sent would keep thousands of them.
    /// </summary>
    [Fact]
    public void The_history_does_not_grow_with_the_race()
    {
        var track = Steady(600);

        Assert.True(track.Held < 40, $"holding {track.Held} places");
    }

    /// <summary>
    /// A pause is not a cadence. The gap across the start barrier is seconds
    /// long, and a delay trained on it starts every race at its ceiling: a
    /// real race opened at 250ms and took until its three-hundredth place to
    /// come down to ninety, which is a quarter second of latency bought to
    /// smooth a pause that had already ended.
    /// </summary>
    [Fact]
    public void A_pause_before_the_race_does_not_set_the_delay()
    {
        var track = new RemoteTrack();
        var arrived = Noon;

        // One place, then the wait at the start line, then driving.
        track.Heard(0, At(0), arrived);
        arrived = arrived.AddMilliseconds(4000);
        track.Heard(4000, At(0), arrived);

        for (int i = 2; i < 10; i++)
        {
            arrived = arrived.AddMilliseconds(33);
            track.Heard(33, At(i * 100), arrived);
        }

        Assert.Equal(RemoteTrack.Least, track.Delay);
    }

    /// <summary>
    /// And neither does a gap in the arrivals, which is the same mistake seen
    /// from the other side.
    ///
    /// A race froze for six seconds over a tunnel. The sender kept to its
    /// thirty-three milliseconds throughout - so the sender's own gaps were
    /// innocent and the guard above let them through - and the first place to
    /// get here afterwards had waited six seconds on the wire. Read as
    /// raggedness, that put the delay at its ceiling for the next lap.
    /// </summary>
    [Fact]
    public void Nor_does_a_gap_in_the_arrivals()
    {
        var track = new RemoteTrack();
        var arrived = Noon;

        for (int i = 0; i < 20; i++)
        {
            if (i > 0) arrived = arrived.AddMilliseconds(33);
            track.Heard(i == 0 ? 0 : 33, At(i * 100), arrived);
        }

        // Six seconds where nothing arrives. What comes out the other side is
        // still one of the sender's ordinary places - it kept sending all
        // along, and the ones in between were lost.
        arrived = arrived.AddMilliseconds(6000);
        track.Heard(33, At(2000), arrived);

        for (int i = 21; i < 30; i++)
        {
            arrived = arrived.AddMilliseconds(33);
            track.Heard(33, At(i * 100), arrived);
        }

        Assert.Equal(RemoteTrack.Least, track.Delay);
    }

    /// <summary>
    /// A late arrival the buffer could actually cover still counts. Guarding
    /// the estimate must not turn into ignoring the raggedness it exists to
    /// measure.
    /// </summary>
    [Fact]
    public void But_lateness_within_the_ceiling_still_counts()
    {
        var track = Steady(60, late: i => i % 2 == 0 ? 3 : 63);

        Assert.True(track.Delay > RemoteTrack.Least,
            $"a jittery link settled on {track.Delay.TotalMilliseconds:F0}ms, the floor");
    }
}
