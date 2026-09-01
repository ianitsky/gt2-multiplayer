using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// A race run to a clock. The parts that can be pinned without the game are the
/// ones about what a room says and what the standings make of it - the ending
/// itself is the game's own, reached by naming the lap the race should end on.
/// </summary>
public class TimedRaceTests
{
    static Room Room(byte laps = 2, ushort minutes = 0) => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        "ian's room", "seattle_short", "special", 6,
        [new Player("ian", "buc9n", true)], laps, minutes);

    [Fact]
    public void A_room_that_says_nothing_is_run_to_laps()
    {
        Assert.False(Room().ByTheClock);
        Assert.True(Room(minutes: 30).ByTheClock);
    }

    [Fact]
    public void The_length_travels_with_the_room()
    {
        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(Room(minutes: 90)), out var back));

        Assert.Equal(90, back.Minutes);
        Assert.True(back.ByTheClock);
    }

    /// <summary>
    /// Three hours is the longest the host may ask for, and it has to survive
    /// the wire: 180 does not fit in a byte, which is why it goes as two.
    /// </summary>
    [Fact]
    public void The_longest_race_survives_the_wire()
    {
        Assert.True(RoomState.TryDeserialise(
            RoomState.Serialise(Room(minutes: TimedRace.Longest)), out var back));

        Assert.Equal(TimedRace.Longest, back.Minutes);
    }

    /// <summary>
    /// A length off a socket is clamped, but zero is left alone: zero is not a
    /// too-short race, it is a race run to laps.
    /// </summary>
    [Fact]
    public void A_length_off_the_wire_is_clamped_but_zero_still_means_laps()
    {
        var wire = RoomState.Serialise(Room(minutes: 600));
        Assert.True(RoomState.TryDeserialise(wire, out var tooLong));
        Assert.Equal(TimedRace.Longest, tooLong.Minutes);

        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(Room()), out var laps));
        Assert.Equal(TimedRace.ByLaps, laps.Minutes);
        Assert.False(laps.ByTheClock);
    }

    /// <summary>
    /// A room opens with the race the create screen chose. This is the one that
    /// would have caught the bug it was written after: Host took a length and
    /// quietly ignored it, so every room came out as the two-lap default and a
    /// race asked for by the clock started by laps.
    /// </summary>
    [Fact]
    public void A_room_opens_with_the_race_it_was_created_for()
    {
        var byTheClock = new Session("ian", () => DateTime.UtcNow);
        byTheClock.Host("timed", "seattle_short", "special", 6, laps: 2, minutes: 15);

        Assert.Equal(15, byTheClock.Current!.Minutes);
        Assert.True(byTheClock.Current!.ByTheClock);

        var byLaps = new Session("ian", () => DateTime.UtcNow);
        byLaps.Host("lapped", "seattle_short", "special", 6, laps: 7);

        Assert.Equal(7, byLaps.Current!.Laps);
        Assert.False(byLaps.Current!.ByTheClock);
    }

    /// <summary>And what it is created with is clamped, like everything else.</summary>
    [Fact]
    public void A_room_cannot_be_created_with_a_length_nobody_may_choose()
    {
        var session = new Session("ian", () => DateTime.UtcNow);
        session.Host("silly", "seattle_short", "special", 6, laps: 0, minutes: 600);

        Assert.Equal(RaceLaps.Fewest, session.Current!.Laps);
        Assert.Equal(TimedRace.Longest, session.Current!.Minutes);
    }

    /// <summary>
    /// And the shortest race the host may ask for is one minute, which exists
    /// so the ending can be watched without waiting five for it.
    /// </summary>
    [Fact]
    public void A_race_can_be_one_minute_long()
    {
        var session = new Session("ian", () => DateTime.UtcNow);
        session.Host("ian's room", "seattle_short", "special");

        session.SetMinutes(1);

        Assert.Equal(1, session.Current!.Minutes);
        Assert.True(session.Current!.ByTheClock);
    }

    [Fact]
    public void The_host_can_ask_for_a_clock_and_take_it_back()
    {
        var session = new Session("ian", () => DateTime.UtcNow);
        session.Host("ian's room", "seattle_short", "special");

        session.SetMinutes(45);
        Assert.Equal(45, session.Current!.Minutes);

        session.SetMinutes(0);
        Assert.False(session.Current!.ByTheClock);
    }

    /// <summary>
    /// A timed race can end with cars on different laps, which is the case the
    /// standings rule was written for: most laps first, then least time. This
    /// is that rule read back as the thing a timed race needs.
    /// </summary>
    [Fact]
    public void Most_laps_in_the_least_time_wins_a_timed_race()
    {
        List<Player> drivers = [new("slow", "buc9n", true), new("quick", "buc9n", true)];

        var standings = RaceStandings.From(drivers, new Dictionary<byte, RaceResult.Finish>
        {
            [0] = new(4, 1_800_000),
            [1] = new(5, 1_805_000),
        });

        Assert.Equal("quick", standings[0].Name);
        Assert.Equal(5, standings[0].Laps);
    }
}
