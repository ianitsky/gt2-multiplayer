using RecompOne.Runtime.Dispatch;

namespace GT2Port.Multiplayer;

/// <summary>
/// Finds out where a stalled race frame is stuck.
///
/// Everything a stall could have been has been measured and ruled out: no
/// files are read during one, the collector pauses for three or four
/// milliseconds across it, and the frame's own thread never once waits for the
/// baton. So the game is running its own code for two and a half seconds, and
/// the only question left is which code.
///
/// Nothing inside the recompiled game can answer that from the inside - a
/// stuck frame is by definition not reaching any hook. So this samples it from
/// the outside: a thread that wakes every few milliseconds, notices the frame
/// has overrun, and writes down whatever the dispatcher last called. Two
/// hundred samples of a busy-wait all name the same handful of addresses, and
/// that handful is the answer.
/// </summary>
public static class StallWatch
{
    /// <summary>How often to look, which is often enough to shape a stall.</summary>
    static readonly TimeSpan Every = TimeSpan.FromMilliseconds(5);

    /// <summary>How long a frame must have run before its whereabouts matter.</summary>
    static readonly TimeSpan Overdue = TimeSpan.FromMilliseconds(200);

    /// <summary>How many addresses to name, most-sampled first.</summary>
    const int Worst = 6;

    static readonly object _gate = new();
    static readonly Dictionary<uint, int> _seen = [];
    static DateTime _frameBegan = DateTime.MaxValue;
    static Thread? _watching;
    static int _samples;

    /// <summary>Called at the start of every race frame.</summary>
    public static void FrameBegins()
    {
        lock (_gate)
        {
            _frameBegan = DateTime.UtcNow;
            _seen.Clear();
            _samples = 0;
        }

        if (_watching is not null) return;
        _watching = new Thread(Watch)
        {
            IsBackground = true,
            Name = "gt2 stall watch",
            Priority = ThreadPriority.AboveNormal,
        };
        _watching.Start();
    }

    /// <summary>
    /// What the game was doing during the frame just ended, or nothing at all
    /// when it was never overdue - which is every frame in a race that runs.
    /// </summary>
    public static string? WhereItWas()
    {
        lock (_gate)
        {
            if (_samples == 0) return null;

            var worst = _seen.OrderByDescending(p => p.Value).Take(Worst)
                .Select(p => $"0x{p.Key:X8}x{p.Value}");
            return $"{_samples} samples: {string.Join(" ", worst)}";
        }
    }

    static void Watch()
    {
        while (true)
        {
            Thread.Sleep(Every);

            lock (_gate)
            {
                if (DateTime.UtcNow - _frameBegan < Overdue) continue;

                uint at = Dispatcher.LastCalled;
                _seen[at] = _seen.TryGetValue(at, out int count) ? count + 1 : 1;
                _samples++;
            }
        }
    }
}
