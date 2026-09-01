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
