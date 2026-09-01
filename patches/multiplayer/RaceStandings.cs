namespace GT2Port.Multiplayer;

/// <summary>Where one driver finished, and what it took them.</summary>
/// <param name="Place">Counted from one, as a person would say it.</param>
/// <param name="Laps">Laps completed, which is what separates the finishers.</param>
/// <param name="Milliseconds">The race time, or 0 from a driver who never reported.</param>
public sealed record Standing(int Place, string Name, string Car, int Laps, int Milliseconds)
{
    /// <summary>The time as the game would show it: m:ss.mmm.</summary>
    public string Clock =>
        Milliseconds <= 0 ? "--:--.---"
        : $"{Milliseconds / 60000}:{Milliseconds % 60000 / 1000:00}.{Milliseconds % 1000:000}";

    /// <summary>Whether this driver's machine ever said how they got on.</summary>
    public bool Reported => Milliseconds > 0;
}

/// <summary>
/// Puts the drivers in the order they finished.
///
/// Each machine times its own race and nobody else's, which is not a choice so
/// much as the only honest arrangement: a remote car is teleported here every
/// frame, so this machine's idea of when it crossed the line is a fact about
/// the network rather than about the race. So every machine reports its own
/// driver's laps and time, keyed by the room's seat, and every machine sorts
/// the same set into the same order.
///
/// The order is most laps first, then least time - which is the only rule that
/// works for a race that can end with cars on different laps, and the same rule
/// a timed race will need when it arrives.
///
/// A driver nobody heard from goes last and keeps the room's order among their
/// own kind. That is a player whose machine crashed or left, and guessing a
/// place for them would be inventing a result.
/// </summary>
public static class RaceStandings
{
    public static List<Standing> From(
        IReadOnlyList<Player> drivers,
        IReadOnlyDictionary<byte, RaceResult.Finish> results)
    {
        var sorted = drivers
            .Select((driver, seat) => (driver, seat,
                     finish: results.TryGetValue((byte)seat, out var f) ? f : default))
            .OrderByDescending(x => x.finish.Milliseconds > 0)
            .ThenByDescending(x => x.finish.Laps)
            .ThenBy(x => x.finish.Milliseconds)
            .ThenBy(x => x.seat)
            .ToList();

        return [.. sorted.Select((x, i) => new Standing(
            i + 1, x.driver.Name, x.driver.Car, x.finish.Laps, x.finish.Milliseconds))];
    }

    /// <summary>
    /// The standings of the race just run, or empty before there has been one.
    ///
    /// Kept here rather than in the room because they are not the room's: the
    /// host does not publish them, every machine works out the same list from
    /// the same reports, and a machine that joins later has no business seeing
    /// a race it was not in.
    /// </summary>
    public static IReadOnlyList<Standing> OfTheLastRace { get; private set; } = [];

    public static void Show(IReadOnlyList<Standing> standings) => OfTheLastRace = standings;

    /// <summary>Forgets the last race, which is what leaving a room does.</summary>
    public static void Forget() => OfTheLastRace = [];
}
