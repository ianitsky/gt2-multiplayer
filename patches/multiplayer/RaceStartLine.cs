using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Holds every machine at the race screen's first frame.
///
/// The barrier used to sit where the race overlay loads, and a measured run
/// showed why that is wrong: released at 20:19:35.266, the race's first phase
/// change came at 20:19:49.514. Fourteen seconds of course, opponents and
/// sounds load after everyone has been told to go, and no two machines take the
/// same fourteen seconds.
///
/// Reading gt2_01 gives a later place with a name. 0x800162A0 is the phase that
/// runs the race, and it calls slot 0x44 of the object at +0x04 - the class the
/// game calls "12RaceMenuLoop", vtable 0x8002EF98. That runs a ScreenViewLoop,
/// which calls the class's slot 0x10 once a frame. So the first call to slot
/// 0x10 is the first frame of the race, and everything the overlay had to set
/// up is behind it.
///
/// Whether it is late enough is a separate question and this answers it too:
/// the file-read count is printed each frame while it is still moving, so a run
/// says how much loading still happens after the line. If the answer is "a lot"
/// the barrier moves again, and the number it moves to will have been measured
/// rather than guessed.
/// </summary>
public static class RaceStartLine
{
    /// <summary>How many frames to report on, which is enough to see loading stop.</summary>
    const int Frames = 120;

    /// <summary>How long the reads must sit still before saying loading is done.</summary>
    static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);

    static readonly bool Watching =
        Environment.GetEnvironmentVariable("GT2_RACE_PHASES") is not (null or "");

    /// <summary>
    /// Off unless GT2_HOLD_AT_OVERLAY asks for the old, too-early barrier.
    /// </summary>
    public static bool HoldsHere =>
        Environment.GetEnvironmentVariable("GT2_HOLD_AT_OVERLAY") is (null or "")
        && !RacePhases.HoldsLater;

    /// <summary>
    /// How long a frame may take before it is worth a line of its own.
    ///
    /// A race frame is meant to be about 16ms. The countdown ending is reported
    /// as three seconds of nothing, so anything near a tenth of a second is
    /// already the thing being looked for, and at that threshold a normal race
    /// says nothing at all.
    /// </summary>
    static readonly TimeSpan TooLong = TimeSpan.FromMilliseconds(100);

    static DateTime _frameBegan;
    static int _readsAtFrameStart;
    static int _stalls;

    static int _frame;
    static bool _held;
    static DateTime _began;
    static int _readsWhenQuiet;
    static DateTime _lastMoved;
    static bool _saidQuiet;

    /// <summary>Pre-hook on the race screen's per-frame step.</summary>
    public static void FrameBegins(CpuContext c, IMemory m)
    {
        int reads = LoadTrace.Reads;
        var now = DateTime.UtcNow;

        if (_frame++ == 0)
        {
            _began = now;
            _lastMoved = now;
            _readsWhenQuiet = reads;

            // Again here, because the roster was empty when the block was
            // built and the question is whether it is ever filled at all or
            // only filled by menus a launch walks past.
            CourseRoster.Say(m, "at the race's first frame");

            if (HoldsHere)
            {
                _held = true;
                Console.Error.WriteLine(
                    $"[line] {now:HH:mm:ss.fff} the race's first frame - holding the room here");
                ModeHook.HoldAtTheLine();
            }
        }

        // Every frame, watched or not: a hitch that only shows up when a
        // switch is set is a hitch nobody reports. Reads are printed beside it
        // because they are what separates "the disc is being read" from "the
        // machine is busy" - two different faults with one symptom.
        if (_frame > 1)
        {
            var took = now - _frameBegan;
            if (took > TooLong)
                Console.Error.WriteLine(
                    $"[stall] {now:HH:mm:ss.fff} frame {_frame - 1} took {took.TotalMilliseconds:F0}ms"
                    + $"  ({reads - _readsAtFrameStart} files read during it,"
                    + $" {(now - _began).TotalSeconds:F1}s into the race,"
                    + $" {++_stalls} so far)");
        }
        _frameBegan = now;
        _readsAtFrameStart = reads;

        if (!Watching) return;

        if (reads != _readsWhenQuiet)
        {
            _readsWhenQuiet = reads;
            _lastMoved = now;
        }
        else if (!_saidQuiet && now - _lastMoved > Quiet)
        {
            _saidQuiet = true;
            Console.Error.WriteLine(
                $"[line] {now:HH:mm:ss.fff} nothing has loaded for {Quiet.TotalMilliseconds:F0}ms"
                + $" - {_frame} frames and {(now - _began).TotalSeconds:F2}s past the line");
        }

        if (_frame <= Frames && !_saidQuiet)
            Console.Error.WriteLine(
                $"[line] {now:HH:mm:ss.fff} +{(now - _began).TotalSeconds,6:F2}s"
                + $"  frame {_frame,3}  {reads} files read");
    }

    /// <summary>Whether this held the room, which is what the log line reports.</summary>
    public static bool Held => _held;
}
