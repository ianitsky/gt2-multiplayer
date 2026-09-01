using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// The grid is the room's own order of players - there is no second list, on
/// purpose, because a second list is one more thing able to disagree with the
/// room. So these are tests about rearranging a room.
/// </summary>
public class GridOrderTests
{
    static Player Driving(string name) => new(name, "buc9n", Ready: true);
    static Player Watching(string name) => new(name, "", Ready: true, Colour: 0, Watching: true);

    static List<Player> Room() =>
        [Driving("ian"), Driving("les"), Driving("guest"), Watching("eye")];

    [Fact]
    public void A_driver_moves_one_place_forward()
    {
        var moved = GridOrder.Move(Room(), "guest", -1);
        Assert.Equal(["ian", "guest", "les", "eye"], moved.Select(p => p.Name));
    }

    [Fact]
    public void And_one_place_back()
    {
        var moved = GridOrder.Move(Room(), "ian", +1);
        Assert.Equal(["les", "ian", "guest", "eye"], moved.Select(p => p.Name));
    }

    /// <summary>
    /// Off either end does nothing rather than wrapping. Somebody clicking "up"
    /// at the front of the grid never means "put me last".
    /// </summary>
    [Fact]
    public void A_move_off_the_end_does_nothing()
    {
        Assert.Equal(["ian", "les", "guest", "eye"],
            GridOrder.Move(Room(), "ian", -1).Select(p => p.Name));
        Assert.Equal(["ian", "les", "guest", "eye"],
            GridOrder.Move(Room(), "guest", +1).Select(p => p.Name));
    }

    /// <summary>
    /// A viewer is not on the grid, so moving one is not a thing that can
    /// happen - and a viewer must not be moved *into* the grid by somebody
    /// else's move, since Seats reads the drivers off the front of the list.
    /// </summary>
    [Fact]
    public void Viewers_keep_the_end_of_the_list()
    {
        var moved = GridOrder.Move(Room(), "les", -1);

        Assert.Equal(["les", "ian", "guest"], Seats.Drivers(moved).Select(p => p.Name));
        Assert.Equal(["eye"], Seats.Viewers(moved).Select(p => p.Name));
        Assert.Equal("eye", moved[^1].Name);
    }

    [Fact]
    public void Moving_somebody_who_is_not_racing_changes_nothing()
    {
        Assert.Equal(["ian", "les", "guest", "eye"],
            GridOrder.Move(Room(), "eye", -1).Select(p => p.Name));
        Assert.Equal(["ian", "les", "guest", "eye"],
            GridOrder.Move(Room(), "nobody", -1).Select(p => p.Name));
    }

    [Fact]
    public void The_winner_of_the_last_race_starts_at_the_front_of_the_next()
    {
        List<Standing> standings =
        [
            new(1, "guest", "buc9n", 2, 139002),
            new(2, "ian", "buc9n", 2, 141456),
            new(3, "les", "buc9n", 1, 150300),
        ];

        var arranged = GridOrder.ByTheLastRace(Room(), standings);

        Assert.Equal(["guest", "ian", "les", "eye"], arranged.Select(p => p.Name));
    }

    /// <summary>
    /// Somebody the standings do not name - who joined after the race, or whose
    /// machine never reported - keeps their place behind those who are named,
    /// rather than being dropped or given a result they did not earn.
    /// </summary>
    [Fact]
    public void A_driver_the_last_race_does_not_name_keeps_their_place_behind()
    {
        List<Player> room = [Driving("ian"), Driving("newcomer"), Driving("les")];
        List<Standing> standings = [new(1, "les", "buc9n", 2, 139002), new(2, "ian", "buc9n", 2, 141456)];

        var arranged = GridOrder.ByTheLastRace(room, standings);

        Assert.Equal(["les", "ian", "newcomer"], arranged.Select(p => p.Name));
    }

    [Fact]
    public void With_no_last_race_the_room_keeps_the_order_it_had()
    {
        Assert.Equal(["ian", "les", "guest", "eye"],
            GridOrder.ByTheLastRace(Room(), []).Select(p => p.Name));
    }
}
