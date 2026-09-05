using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// The order a race ended in. Every machine works this out for itself from the
/// same reports, so the rule has to give the same answer everywhere - including
/// when a race ends with cars on different laps, and when a machine says
/// nothing at all.
/// </summary>
public class RaceStandingsTests
{
    static Player Driving(string name) => new(name, "buc9n", Ready: true);

    static Dictionary<byte, RaceResult.Finish> Reported(params (byte Seat, int Laps, int Ms)[] said) =>
        said.ToDictionary(x => x.Seat, x => new RaceResult.Finish(x.Laps, x.Ms));

    /// <summary>
    /// A seat only means anything against the order that produced it.
    ///
    /// Two machines showed the same race with the names on each other's times:
    /// one said inmor's best lap was 0:46.400 and the other said it was
    /// 0:46.733, which was the other player's. The times were right on both
    /// and in the same order - only the names had moved, because the room's
    /// player order can be changed by hand while the results are still
    /// arriving, and each machine redraws the table when a report reaches it
    /// rather than at some shared moment.
    ///
    /// This is the shape of that: the same reports read against a reordered
    /// list give every driver somebody else's race. Nothing here can prevent
    /// it - the caller has to hand over the seating the race ran under - so
    /// this pins down what goes wrong when it does not.
    /// </summary>
    [Fact]
    public void Reading_seats_against_a_reordered_list_gives_everybody_the_wrong_race()
    {
        var reported = Reported((0, 2, 108208), (1, 2, 198577));

        List<Player> asRaced = [Driving("host"), Driving("guest")];
        List<Player> reordered = [Driving("guest"), Driving("host")];

        var right = RaceStandings.From(asRaced, reported);
        var wrong = RaceStandings.From(reordered, reported);

        // The same two times, in the same order, on the other two names.
        Assert.Equal(["host", "guest"], right.Select(x => x.Name));
        Assert.Equal(["guest", "host"], wrong.Select(x => x.Name));
        Assert.Equal(right.Select(x => x.Milliseconds), wrong.Select(x => x.Milliseconds));
    }

    [Fact]
    public void The_quickest_over_the_same_laps_wins()
    {
        List<Player> drivers = [Driving("ian"), Driving("les"), Driving("guest")];

        var standings = RaceStandings.From(drivers,
            Reported((0, 2, 141456), (1, 2, 139002), (2, 2, 150300)));

        Assert.Equal(["les", "ian", "guest"], standings.Select(x => x.Name));
        Assert.Equal([1, 2, 3], standings.Select(x => x.Place));
    }

    /// <summary>
    /// Laps first, and it has to be: a car a lap down can be quicker over the
    /// distance it covered, and a rule that sorted on time alone would put it
    /// ahead of the car that beat it.
    /// </summary>
    [Fact]
    public void More_laps_beats_a_quicker_time()
    {
        List<Player> drivers = [Driving("ian"), Driving("les")];

        var standings = RaceStandings.From(drivers,
            Reported((0, 1, 60000), (1, 2, 141456)));

        Assert.Equal("les", standings[0].Name);
        Assert.Equal(2, standings[0].Laps);
    }

    /// <summary>
    /// A driver whose machine never reported goes last whatever their seat,
    /// because there is no result to place them by and inventing one would be
    /// worse than saying so.
    /// </summary>
    [Fact]
    public void A_driver_who_never_reported_goes_last()
    {
        List<Player> drivers = [Driving("silent"), Driving("ian"), Driving("les")];

        var standings = RaceStandings.From(drivers,
            Reported((1, 2, 141456), (2, 2, 139002)));

        Assert.Equal(["les", "ian", "silent"], standings.Select(x => x.Name));
        Assert.False(standings[2].Reported);
        Assert.Equal("--:--.---", standings[2].Clock);
    }

    /// <summary>
    /// Two machines that never spoke keep the room's order between them, so the
    /// list is the same on every machine rather than however a dictionary
    /// happened to enumerate.
    /// </summary>
    [Fact]
    public void Drivers_who_never_reported_keep_the_rooms_order()
    {
        List<Player> drivers = [Driving("first"), Driving("second"), Driving("ian")];

        var standings = RaceStandings.From(drivers, Reported((2, 2, 141456)));

        Assert.Equal(["ian", "first", "second"], standings.Select(x => x.Name));
    }

    [Fact]
    public void A_time_reads_the_way_the_game_shows_it()
    {
        var standings = RaceStandings.From([Driving("ian")], Reported((0, 2, 141456)));
        Assert.Equal("2:21.456", standings[0].Clock);
    }
}
