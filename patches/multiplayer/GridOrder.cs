namespace GT2Port.Multiplayer;

/// <summary>
/// The order the room's drivers line up in.
///
/// There is no separate list of grid positions, and there should not be: the
/// room's own order of players *is* the grid. RaceGrid already numbers each
/// entrant from where its player sits in the room, and the wire already keys
/// everything by that seat, so a second list saying the same thing in a
/// different order would be one more thing able to disagree with the room.
///
/// So arranging the grid is rearranging the room, and it is the host's to do -
/// the host publishes the room, everyone else reads it. Seats move when it
/// happens, which is safe in the lobby and nowhere else: by the time a race is
/// built, every machine has the same room.
///
/// Viewers are not on the grid and are not moved. They keep the end of the
/// list, which is where <see cref="Seats"/> already expects to find them.
/// </summary>
public static class GridOrder
{
    /// <summary>
    /// Moves one driver up or down the grid, keeping everyone else in the order
    /// they were in.
    ///
    /// <paramref name="by"/> is a number of places: -1 for one place forward,
    /// +1 for one place back. A move off either end does nothing rather than
    /// wrapping, because a wrap is never what somebody clicking "up" at the
    /// front of the grid meant.
    /// </summary>
    public static List<Player> Move(IReadOnlyList<Player> players, string who, int by)
    {
        var drivers = Seats.Drivers(players);
        var viewers = Seats.Viewers(players);

        int from = drivers.FindIndex(p => p.Name == who);
        if (from < 0) return [.. players];

        int to = from + by;
        if (to < 0 || to >= drivers.Count) return [.. players];

        var moved = drivers[from];
        drivers.RemoveAt(from);
        drivers.Insert(to, moved);

        return [.. drivers, .. viewers];
    }

    /// <summary>
    /// Puts the grid in the order the last race finished.
    ///
    /// The winner starts at the front of the next one. A driver the standings
    /// do not name - one who joined after the race, or whose machine never
    /// reported - keeps their place behind those who are named, in the order
    /// the room already had them, rather than being dropped or guessed at.
    ///
    /// This is a default and not a decision: the host can move anybody
    /// afterwards, which is the whole point of it being the room's own order.
    /// </summary>
    public static List<Player> ByTheLastRace(
        IReadOnlyList<Player> players, IReadOnlyList<Standing> standings)
    {
        if (standings.Count == 0) return [.. players];

        var drivers = Seats.Drivers(players);
        var viewers = Seats.Viewers(players);

        var finished = standings
            .Select(s => drivers.FirstOrDefault(p => p.Name == s.Name))
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        var unnamed = drivers.Where(p => !finished.Any(f => f.Name == p.Name));

        return [.. finished, .. unnamed, .. viewers];
    }
}
