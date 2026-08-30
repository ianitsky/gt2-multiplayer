using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// Every machine puts its own player in entrant 0, so the same car is a
/// different slot on every machine. Anything sent between them has to be keyed
/// by a seat in the room, and turned back into a slot on arrival - and getting
/// that rotation wrong would put a remote player's car on top of somebody
/// else's.
/// </summary>
public class RaceGridOrderTests
{
    static IReadOnlyList<Player> Room(params string[] names) =>
        names.Select(n => new Player(n, "bjkrn", true)).ToList();

    [Fact]
    public void PutsTheLocalPlayerFirstWhereverTheRoomHasThem()
    {
        var room = Room("ana", "bruno", "clara");

        Assert.Equal(["clara", "ana", "bruno"],
            RaceGrid.Order(room, "clara", 3).Select(p => p.Name));
    }

    [Fact]
    public void LeavesTheOrderAloneForWhoeverIsAlreadyFirst()
    {
        var room = Room("ana", "bruno", "clara");

        Assert.Equal(["ana", "bruno", "clara"],
            RaceGrid.Order(room, "ana", 3).Select(p => p.Name));
    }

    /// <summary>
    /// The same pair of players, seen from both machines: each drives slot 0
    /// and puts the other somewhere else. If both agreed on a slot, one of them
    /// would be steering the other's car.
    /// </summary>
    [Fact]
    public void GivesEachMachineItsOwnRotation()
    {
        var room = Room("ana", "bruno");

        Assert.Equal(0, RaceGrid.SlotFor(room, "ana", "ana"));
        Assert.Equal(1, RaceGrid.SlotFor(room, "ana", "bruno"));
        Assert.Equal(0, RaceGrid.SlotFor(room, "bruno", "bruno"));
        Assert.Equal(1, RaceGrid.SlotFor(room, "bruno", "ana"));
    }

    [Fact]
    public void SaysSoWhenAPlayerIsNotRacing()
    {
        var room = Room("ana", "bruno");

        Assert.Equal(-1, RaceGrid.SlotFor(room, "ana", "clara"));
    }
}
