using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// A seat used to be a place in the room, and viewers make that wrong: they sit
/// in the room and not in the race, so a pose keyed by a place in the room
/// arrives at somebody else's car. These pin the split - seats count along the
/// drivers, and the grid is built around whoever this machine is following.
/// </summary>
public class ViewerSeatTests
{
    static Player Driving(string name, string car = "dvpgn") => new(name, car, Ready: true);
    static Player Watching(string name) => new(name, "", Ready: true, Colour: 0, Watching: true);

    [Fact]
    public void A_viewer_is_in_the_room_and_not_among_the_drivers()
    {
        List<Player> room = [Driving("ian"), Watching("guest"), Driving("les")];

        Assert.Equal(["ian", "les"], Seats.Drivers(room).Select(p => p.Name));
        Assert.Equal(["guest"], Seats.Viewers(room).Select(p => p.Name));
    }

    /// <summary>
    /// The seat is the thing on the wire, so a viewer sitting between two
    /// drivers must not push the second one's number along - both machines
    /// would then key the same car differently.
    /// </summary>
    [Fact]
    public void A_viewer_between_two_drivers_does_not_move_the_second_ones_seat()
    {
        List<Player> room = [Driving("ian"), Watching("guest"), Driving("les")];

        Assert.Equal(0, Seats.Of(room, "ian"));
        Assert.Equal(1, Seats.Of(room, "les"));
    }

    /// <summary>
    /// A viewer has no car, so there is nothing for a pose about them to mean.
    /// The same answer an unknown name gets, on purpose.
    /// </summary>
    [Fact]
    public void A_viewer_holds_no_seat()
    {
        List<Player> room = [Driving("ian"), Watching("guest")];

        Assert.Equal(-1, Seats.Of(room, "guest"));
        Assert.Equal(-1, Seats.Of(room, "nobody"));
    }

    /// <summary>
    /// Six is what the race block has room for. A seventh driver is not a
    /// driver, whatever the room says, or the grid would be written past its
    /// end.
    /// </summary>
    [Fact]
    public void Only_six_of_them_are_driving()
    {
        List<Player> room = [.. Enumerable.Range(0, 8).Select(i => Driving($"p{i}"))];

        Assert.Equal(RaceGrid.Slots, Seats.Drivers(room).Count);
        Assert.Equal(-1, Seats.Of(room, "p6"));
    }

    // ---- the grid a viewer builds ----

    /// <summary>
    /// A viewer's race is the race the driver they are watching would build.
    /// Entrant 0 is what the view follows, so the watched driver goes there.
    /// </summary>
    [Fact]
    public void A_viewer_leads_the_grid_with_the_driver_it_follows()
    {
        List<Player> drivers = [Driving("ian"), Driving("les"), Driving("kay")];

        var order = RaceGrid.Order(drivers, leader: "les", racing: 3);

        Assert.Equal(["les", "ian", "kay"], order.Select(p => p.Name));
    }

    /// <summary>
    /// And the rest keep the room's own order behind the leader, because that
    /// is the ordering every machine already agrees on.
    /// </summary>
    [Fact]
    public void Everyone_else_keeps_the_rooms_order()
    {
        List<Player> drivers = [Driving("a"), Driving("b"), Driving("c"), Driving("d")];

        Assert.Equal(["d", "a", "b", "c"], RaceGrid.Order(drivers, "d", 4).Select(p => p.Name));
    }

    /// <summary>
    /// A leader who is not driving - a viewer's own name, or a driver who has
    /// left - leaves the order alone rather than throwing. The first driver is
    /// then at entrant 0, which is the fallback the session already picks.
    /// </summary>
    [Fact]
    public void A_leader_who_is_not_driving_leaves_the_order_alone()
    {
        List<Player> drivers = [Driving("ian"), Driving("les")];

        Assert.Equal(["ian", "les"], RaceGrid.Order(drivers, "guest", 2).Select(p => p.Name));
    }

    /// <summary>
    /// The slot a driver holds is what a pose is written into, and it differs
    /// between a viewer following one driver and a viewer following another -
    /// which is exactly why the wire carries seats and not slots.
    /// </summary>
    [Fact]
    public void The_same_driver_is_a_different_slot_on_two_viewers_machines()
    {
        List<Player> drivers = [Driving("ian"), Driving("les"), Driving("kay")];

        Assert.Equal(0, RaceGrid.SlotFor(drivers, "ian", "ian"));
        Assert.Equal(1, RaceGrid.SlotFor(drivers, "les", "ian"));
    }
}
