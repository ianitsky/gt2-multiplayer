using System.Collections.Concurrent;

namespace GT2Relay;

/// <summary>
/// Says something on the console without the console being able to stop the
/// relay.
///
/// A write to a Windows console can block indefinitely. Clicking in the window
/// puts it in selection mode and every writer waits until the selection is
/// cleared - so a status line written from the serving loop is a relay that
/// stops relaying the moment somebody clicks on its window to read it. It goes
/// on holding its port and answering nothing: bound, alive and deaf, which is
/// hard to tell from a crash and impossible to tell from the outside.
///
/// That is not hypothetical. It happened to this program the same day a status
/// line was added to the sweep, turning a process that printed twice at
/// startup and never again into one that writes whenever traffic moves.
///
/// So the line goes into a queue and one thread that does nothing else writes
/// it. If the console blocks, that thread blocks and nothing else does. The
/// queue is bounded, so a window left selected for an hour costs a fixed
/// number of lines rather than all of memory, and lines over the cap are
/// dropped - a dropped status line is a loss nobody can measure, and a relay
/// that stopped forwarding is one everybody can.
/// </summary>
static class Say
{
    /// <summary>
    /// How many lines may be waiting. Two hundred and fifty six is several
    /// minutes of sweeps, which is longer than anybody reads a console for
    /// before letting go of it.
    /// </summary>
    const int Most = 256;

    static readonly BlockingCollection<string> Waiting = new(Most);

    static Say()
    {
        var writer = new Thread(Write)
        {
            IsBackground = true,
            Name = "gt2relay console",
        };
        writer.Start();
    }

    static void Write()
    {
        foreach (var line in Waiting.GetConsumingEnumerable())
        {
            try { Console.WriteLine(line); }
            catch (IOException) { /* the console went away; the relay has not */ }
        }
    }

    /// <summary>
    /// Queues a line, or drops it if the writer has fallen that far behind.
    /// Never waits, which is the whole point.
    /// </summary>
    public static void Line(string text) => Waiting.TryAdd(text);

    /// <summary>
    /// Lets the writer finish what is queued, for the few lines said on the
    /// way out. Bounded, because a console that is blocked now was blocked a
    /// moment ago and waiting on it forever would make stopping the relay as
    /// hard as this made serving from it.
    /// </summary>
    public static void Finish()
    {
        Waiting.CompleteAdding();
        Thread.Sleep(100);
    }
}
