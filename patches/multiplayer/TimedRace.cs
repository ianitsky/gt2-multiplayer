using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Runs a race to a clock instead of to a lap count.
///
/// Nothing here ends a race. The game already knows how to end one - it does it
/// when a car completes the lap the record's +0x0F names - and "the cars finish
/// the lap they are on" is exactly what that does. So a timed race is a lap
/// race whose lap count is not decided until the time runs out:
///
///   * it starts with the highest lap count the byte will hold, so the game has
///     no reason to end it early;
///   * when the clock runs out, +0x0F is set to the lap this car is on plus
///     one, and the game ends the race when that lap is completed.
///
/// Which means the ending is the game's own, with its own results screen and
/// its own timing, rather than something this port forces. The alternative -
/// driving the race loop into its finished state from outside - would have to
/// reproduce all of that, and would be a second way for a race to end.
///
/// Every machine does this for its own car, and they will not all reach it in
/// the same instant. That is already true of a lap race and already handled:
/// results are exchanged from the lobby, for as long as it takes, in whatever
/// order the machines get back.
///
/// **The clock itself is the game's**, not a stopwatch here. Which of the
/// game's two is unsettled: 0x801D5F80 holds the race time in milliseconds at
/// the end of a race, but whether it ticks *during* one has never been watched;
/// 0x800A8C64 rises by exactly two a frame and the screen's clock matches
/// frames times two over sixty. So this uses the counter it can prove is
/// running and reports both, and one timed race settles which to keep.
/// </summary>
public static class TimedRace
{
    /// <summary>The shortest and longest race the host may ask for, in minutes.</summary>
    public const int Shortest = 5;
    public const int Longest = 180;

    /// <summary>A race that is not run to a clock at all.</summary>
    public const ushort ByLaps = 0;

    /// <summary>Clamps a length to what the host may choose.</summary>
    public static int Sensible(int minutes) => Math.Clamp(minutes, Shortest, Longest);

    /// <summary>
    /// The counter that rises by two every frame, which is the rate the screen's
    /// own clock runs at: a race shown as 2:21.456 had run frames * 2 / 60
    /// seconds. Free-running from before the lights, so only the difference
    /// since the race began means anything.
    /// </summary>
    const uint Sixtieths = 0x800A8C64u;

    /// <summary>How many of those go by in a second.</summary>
    const int PerSecond = 60;

    static int _minutes;
    static int _began = -1;
    static bool _called;

    /// <summary>Whether the race now running is being run to a clock.</summary>
    public static bool Running => _minutes > 0;

    /// <summary>
    /// Starts the clock, at the race's first frame. The reading is kept rather
    /// than assumed to be zero, because this counter has been running since
    /// well before the race.
    /// </summary>
    public static void Begins(IMemory m, int minutes)
    {
        _minutes = minutes;
        _began = minutes > 0 ? (int)m.ReadU32(Sixtieths) : -1;
        _called = false;

        if (minutes > 0)
            Console.Error.WriteLine(
                $"[timed] a {minutes} minute race - the clock reads {_began} at the first frame");
    }

    /// <summary>Forgets the race just run.</summary>
    public static void Forget()
    {
        _minutes = 0;
        _began = -1;
        _called = false;
    }

    /// <summary>How long this race has been running, in seconds.</summary>
    public static int SecondsSoFar(IMemory m) =>
        _began < 0 ? 0 : ((int)m.ReadU32(Sixtieths) - _began) / PerSecond;

    /// <summary>
    /// Called once a frame. Calls the last lap when the time is up, and does
    /// nothing else ever.
    /// </summary>
    public static void Tick(IMemory m)
    {
        if (!Running || _called) return;
        if (SecondsSoFar(m) < _minutes * 60) return;

        _called = true;

        // The lap this car is on, plus one. RaceResult.Laps counts laps
        // completed, so a car that has finished two is on its third - and the
        // race should end when that third is done.
        int completed = (short)m.ReadU16(RemoteCars.FirstCarObject + RaceResult.Laps);
        int last = Math.Clamp(completed + 1, RaceLaps.Fewest, RaceLaps.Most);

        RaceLaps.CallTheLastLap(m, last);

        Console.Error.WriteLine(
            $"[timed] {_minutes} minute(s) are up after {SecondsSoFar(m)}s"
            + $" - this car has completed {completed} lap(s), so the race ends on lap {last}"
            + $" (0x801D5F80 reads {(int)m.ReadU32(RaceResult.Milliseconds)})");
    }
}
