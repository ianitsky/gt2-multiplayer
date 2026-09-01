namespace GT2Port.Multiplayer;

/// <summary>Where one driver finished, and what it took them.</summary>
/// <param name="Place">Counted from one, as a person would say it.</param>
/// <param name="Laps">Laps completed, which is what separates the finishers.</param>
/// <param name="Milliseconds">The race time, or 0 from a driver who never reported.</param>
public sealed record Standing(int Place, string Name, string Car, int Laps, int Milliseconds,
                              int BestLapMilliseconds = 0)
{
    /// <summary>The race time as the game would show it: m:ss.mmm.</summary>
    public string Clock => Show(Milliseconds);

    /// <summary>And their best lap of it.</summary>
    public string BestLap => Show(BestLapMilliseconds);

    /// <summary>Whether this driver's machine ever said how they got on.</summary>
    public bool Reported => Milliseconds > 0 || BestLapMilliseconds > 0;

    static string Show(int ms) =>
        ms <= 0 ? "--:--.---"
        : $"{ms / 60000}:{ms % 60000 / 1000:00}.{ms % 1000:000}";
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
    /// <summary>
    /// Puts the drivers in order - of finishing, or of their best lap when the
    /// session was a qualifying one.
    ///
    /// Two different questions, and they need different rules. A race asks who
    /// got furthest quickest, so laps come first and time breaks the tie. A
    /// qualifying session asks nothing about distance at all: everybody runs
    /// the same two laps and only the best of them counts, so a driver who
    /// spun on one lap and was quickest on the other qualifies on the quick one.
    /// </summary>
    public static List<Standing> From(
        IReadOnlyList<Player> drivers,
        IReadOnlyDictionary<byte, RaceResult.Finish> results,
        bool qualifying = false)
    {
        var said = drivers
            .Select((driver, seat) => (driver, seat,
                     finish: results.TryGetValue((byte)seat, out var f) ? f : default));

        var sorted = qualifying
            ? said.OrderByDescending(x => x.finish.HasABestLap)
                  .ThenBy(x => x.finish.BestLapMilliseconds)
                  .ThenBy(x => x.seat)
                  .ToList()
            : said.OrderByDescending(x => x.finish.Milliseconds > 0)
                  .ThenByDescending(x => x.finish.Laps)
                  .ThenBy(x => x.finish.Milliseconds)
                  .ThenBy(x => x.seat)
                  .ToList();

        return [.. sorted.Select((x, i) => new Standing(
            i + 1, x.driver.Name, x.driver.Car, x.finish.Laps, x.finish.Milliseconds,
            x.finish.BestLapMilliseconds))];
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

    /// <summary>Whether what is shown above is a qualifying session's.</summary>
    public static bool WereQualifying { get; private set; }

    public static void Show(IReadOnlyList<Standing> standings, bool qualifying = false)
    {
        OfTheLastRace = standings;
        WereQualifying = qualifying;
    }

    /// <summary>Forgets the last race, which is what leaving a room does.</summary>
    public static void Forget()
    {
        OfTheLastRace = [];
        WereQualifying = false;
    }
}
