using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// Handing this machine's car to the game, so a race of four can be tested by
/// one person.
///
/// Marking the entrant as the game's did nothing, and the port already knew
/// why: the pad drives entrant zero whatever the entrant says. So the lever is
/// the one that is already true - if the pad takes entrant zero, put this
/// machine's car somewhere else and it becomes one of the five the game already
/// drives.
///
/// Which means "my car" stops meaning "slot zero", and that is what these pin.
/// </summary>
public class GameDrivesTests
{
    static Player Driving(string name) => new(name, "buc9n", Ready: true);

    static List<Player> Room() => [Driving("ian"), Driving("les"), Driving("guest")];

    static DirectRace.Pending Race(string leader, string driving) =>
        new(Room(), leader, "buc9n", "seattle_short", null, Driving: driving);

    [Fact]
    public void An_ordinary_race_puts_this_machines_car_on_entrant_zero()
    {
        Assert.Equal(0, Race(leader: "ian", driving: "ian").MySlot);
        Assert.Equal("ian", Race(leader: "ian", driving: "ian").MyName);
    }

    /// <summary>
    /// And a race this machine is only watching keeps the old arrangement,
    /// where the leader is the driver being followed and there is no car of
    /// this machine's own at all.
    /// </summary>
    [Fact]
    public void A_race_with_nothing_said_about_driving_still_means_slot_zero()
    {
        var race = new DirectRace.Pending(Room(), "les", "buc9n", "seattle_short", null);

        Assert.Equal(0, race.MySlot);
        Assert.Equal("les", race.MyName);
    }

    /// <summary>
    /// When the game drives, the grid is led by somebody else and this
    /// machine's car is wherever the rotation left it - which is the slot every
    /// reader of "my car" has to ask for.
    /// </summary>
    [Fact]
    public void When_the_game_drives_this_machines_car_is_not_on_entrant_zero()
    {
        var race = Race(leader: "les", driving: "ian");

        Assert.NotEqual(0, race.MySlot);
        Assert.Equal("ian", race.MyName);

        // Order moves the leader to the front and leaves everyone else in the
        // room's order behind them, so the grid reads les, ian, guest.
        Assert.Equal(1, race.MySlot);
    }

    /// <summary>
    /// The slot is always the grid's answer and never a shortcut, because when
    /// the game is driving the room does not begin at entrant zero at all: a
    /// room of one would then say slot zero while its car was written into
    /// entrant two.
    /// </summary>
    [Fact]
    public void Even_a_leader_driving_their_own_car_asks_the_grid_where_it_is()
    {
        var race = Race(leader: "ian", driving: "ian");

        Assert.Equal(RaceGrid.SlotFor(Room(), "ian", "ian"), race.MySlot);
        Assert.Equal(RaceGrid.FirstRoomEntrant, race.MySlot);
    }

    [Fact]
    public void And_the_slot_it_names_is_the_one_the_grid_agrees_on()
    {
        var race = Race(leader: "guest", driving: "ian");

        Assert.Equal(
            RaceGrid.SlotFor(Room(), "guest", "ian"),
            race.MySlot);
    }

    /// <summary>
    /// A driver the race does not have cannot be given a slot, and a negative
    /// one would be read straight out of the front of the car array.
    /// </summary>
    [Fact]
    public void A_driver_the_race_does_not_have_falls_back_to_zero()
    {
        var race = Race(leader: "ian", driving: "nobody");

        Assert.Equal(0, race.MySlot);
    }
}
