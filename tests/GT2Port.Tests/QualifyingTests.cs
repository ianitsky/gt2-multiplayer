using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// The session that sets the grid before the race. Almost all of it is the race
/// machinery again, so what is worth pinning is the three places it differs:
/// two laps whatever the room says, scored on the best lap rather than on
/// distance, and a room that moves on afterwards instead of staying put.
/// </summary>
public class QualifyingTests
{
    static Player Driving(string name) => new(name, "buc9n", Ready: true);

    static Session Hosting(bool qualifying) =>
        Made(new Session("ian", () => DateTime.UtcNow), qualifying);

    static Session Made(Session session, bool qualifying)
    {
        session.Host("ian's room", "seattle_short", "special", 6,
                     laps: 30, minutes: 0, qualifying: qualifying);
        return session;
    }

    [Fact]
    public void A_room_runs_no_qualifying_unless_it_was_asked_for()
    {
        Assert.False(Hosting(qualifying: false).Current!.QualifyingNext);
        Assert.True(Hosting(qualifying: true).Current!.QualifyingNext);
    }

    /// <summary>
    /// Two laps, and not the room's thirty. A room's own length is what the
    /// race is; qualifying borrowing it would let a three-hour qualifying
    /// session be asked for by accident.
    /// </summary>
    [Fact]
    public void Qualifying_is_two_laps_whatever_the_room_races_over()
    {
        var room = Hosting(qualifying: true).Current!;

        Assert.Equal(Qualifying.Laps, room.LapsNext);
        Assert.Equal(TimedRace.ByLaps, room.MinutesNext);
        Assert.Equal(30, room.Laps);
    }

    [Fact]
    public void And_a_timed_room_still_qualifies_over_laps()
    {
        var session = new Session("ian", () => DateTime.UtcNow);
        session.Host("timed", "seattle_short", "special", 6, minutes: 60, qualifying: true);

        Assert.Equal(Qualifying.Laps, session.Current!.LapsNext);
        Assert.Equal(TimedRace.ByLaps, session.Current!.MinutesNext);
        Assert.Equal(60, session.Current!.Minutes);
    }

    [Fact]
    public void Once_qualifying_is_over_the_room_is_arranging_a_race()
    {
        var session = Hosting(qualifying: true);

        session.QualifyingIsOver();

        Assert.False(session.Current!.QualifyingNext);
        Assert.Equal(30, session.Current!.LapsNext);
    }

    /// <summary>
    /// And nothing puts it back. The grid qualifying produced is what the race
    /// is about to use, so a room that fell back to qualifying would be
    /// throwing that away.
    /// </summary>
    [Fact]
    public void And_nothing_puts_the_room_back_to_qualifying()
    {
        var session = Hosting(qualifying: true);
        session.QualifyingIsOver();
        session.QualifyingIsOver();

        Assert.False(session.Current!.QualifyingNext);
    }

    [Fact]
    public void The_stage_travels_with_the_room()
    {
        var room = Hosting(qualifying: true).Current!;

        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(room), out var back));
        Assert.True(back.QualifyingNext);
        Assert.Equal(Qualifying.Laps, back.LapsNext);
    }

    /// <summary>
    /// Qualifying is scored on the best lap alone. A driver who spun on one lap
    /// and was quickest on the other qualifies on the quick one, and how far
    /// anybody got does not come into it.
    /// </summary>
    [Fact]
    public void The_quickest_lap_qualifies_however_the_rest_of_it_went()
    {
        List<Player> drivers = [Driving("steady"), Driving("spun")];

        var standings = RaceStandings.From(drivers, new Dictionary<byte, RaceResult.Finish>
        {
            [0] = new(2, 180_000, BestLapMilliseconds: 88_000),
            [1] = new(2, 240_000, BestLapMilliseconds: 85_500),
        }, qualifying: true);

        Assert.Equal(["spun", "steady"], standings.Select(x => x.Name));
        Assert.Equal("1:25.500", standings[0].BestLap);
    }

    /// <summary>
    /// And a driver who never finished a lap has no time to qualify on, so they
    /// go last rather than first with a zero.
    /// </summary>
    [Fact]
    public void A_driver_with_no_lap_qualifies_last()
    {
        List<Player> drivers = [Driving("nolap"), Driving("quick")];

        var standings = RaceStandings.From(drivers, new Dictionary<byte, RaceResult.Finish>
        {
            [0] = new(0, 0, BestLapMilliseconds: 0),
            [1] = new(2, 180_000, BestLapMilliseconds: 88_000),
        }, qualifying: true);

        Assert.Equal(["quick", "nolap"], standings.Select(x => x.Name));
        Assert.Equal("--:--.---", standings[1].BestLap);
    }

    /// <summary>
    /// The grid for the race is then that order, which is what a qualifying
    /// session is for.
    /// </summary>
    [Fact]
    public void The_grid_for_the_race_is_the_qualifying_order()
    {
        List<Player> room = [Driving("slow"), Driving("quick")];

        var standings = RaceStandings.From(room, new Dictionary<byte, RaceResult.Finish>
        {
            [0] = new(2, 180_000, BestLapMilliseconds: 91_000),
            [1] = new(2, 181_000, BestLapMilliseconds: 88_000),
        }, qualifying: true);

        Assert.Equal(["quick", "slow"],
            GridOrder.ByTheLastRace(room, standings).Select(p => p.Name));
    }

    [Fact]
    public void A_race_is_still_scored_on_distance_and_time()
    {
        List<Player> drivers = [Driving("quickest lap"), Driving("won")];

        var standings = RaceStandings.From(drivers, new Dictionary<byte, RaceResult.Finish>
        {
            [0] = new(1, 90_000, BestLapMilliseconds: 80_000),
            [1] = new(2, 180_000, BestLapMilliseconds: 88_000),
        });

        Assert.Equal("won", standings[0].Name);
    }

    [Fact]
    public void The_lobby_says_which_session_it_is_arranging()
    {
        var qualifying = Hosting(qualifying: true).Current!;
        Assert.Equal("Qualifying", Qualifying.Title(qualifying));
        Assert.Equal("Start qualifying", Qualifying.StartSays(qualifying));

        var racing = Hosting(qualifying: false).Current!;
        Assert.Equal("Race", Qualifying.Title(racing));
        Assert.Equal("Start race", Qualifying.StartSays(racing));
    }
}
