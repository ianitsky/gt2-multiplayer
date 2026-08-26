namespace GT2Port.Multiplayer;

/// <summary>
/// Times what a race does between loading and running.
///
/// The start barrier holds every machine at the moment the race overlay loads,
/// and that turns out to be too early: both machines report a short wait and
/// still start apart, because after the barrier each of them loads its own
/// course and its own opponents, and they take different lengths of time. So
/// the barrier has to move to a point after loading and before the countdown -
/// and nothing so far says where in real time that point is, or how much room
/// there is between the two.
///
/// This measures it. Files read is the only cheap sign of loading from
/// outside: while a race is loading the count keeps moving, and when it stops
/// the loading has finished. Printed against the clock, the gap between the
/// last read and the countdown appearing on screen is the window the barrier
/// has to fit in.
///
/// Off unless GT2_RACE_CLOCK is set. It is an instrument, not a feature.
/// </summary>
public static class RaceClock
{
    static readonly bool Ticking =
        Environment.GetEnvironmentVariable("GT2_RACE_CLOCK") is not (null or "");

    /// <summary>How often to print, which is often enough to see loading stop.</summary>
    static readonly TimeSpan Every = TimeSpan.FromMilliseconds(250);

    /// <summary>How long after the last file read to stop printing.</summary>
    static readonly TimeSpan Enough = TimeSpan.FromSeconds(25);

    static DateTime? _began;
    static DateTime _lastPrint;
    static int _readsAtLastPrint;
    static DateTime _lastRead;

    /// <summary>Starts the clock; called when the race overlay is loaded.</summary>
    public static void Begin()
    {
        if (!Ticking) return;

        _began = DateTime.UtcNow;
        _lastPrint = _began.Value;
        _lastRead = _began.Value;
        _readsAtLastPrint = LoadTrace.Reads;
        Console.Error.WriteLine($"[clock] {_began:HH:mm:ss.fff} the race overlay is loaded");
    }

    /// <summary>Called once a frame; prints while anything is still happening.</summary>
    public static void Tick()
    {
        if (!Ticking || _began is not { } began) return;

        var now = DateTime.UtcNow;
        int reads = LoadTrace.Reads;
        if (reads != _readsAtLastPrint) _lastRead = now;

        if (now - _lastPrint < Every) return;
        _lastPrint = now;

        // Stopping matters as much as starting: a clock that runs all race
        // buries the moment it was meant to show under a thousand lines.
        if (now - _lastRead > Enough)
        {
            _began = null;
            Console.Error.WriteLine(
                $"[clock] nothing more has loaded for {Enough.TotalSeconds:F0}s - stopping");
            return;
        }

        Console.Error.WriteLine(
            $"[clock] {now:HH:mm:ss.fff} +{(now - began).TotalSeconds,6:F2}s"
            + $"  {reads} files read"
            + $"  (quiet for {(now - _lastRead).TotalSeconds:F2}s)");

        _readsAtLastPrint = reads;
    }
}
