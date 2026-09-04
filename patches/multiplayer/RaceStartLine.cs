using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Holds every machine at the same frame of the race screen - the fourth,
/// GT2_START_AT_FRAME for any other.
///
/// The barrier used to sit where the race overlay loads, and a measured run
/// showed why that is wrong: released at 20:19:35.266, the race's first phase
/// change came at 20:19:49.514. Fourteen seconds of course, opponents and
/// sounds load after everyone has been told to go, and no two machines take the
/// same fourteen seconds.
///
/// The frame it holds at is counted in FrameEnds, not in FrameBegins. That is
/// not a detail: FrameBegins is hooked on slot 0x10, which runs once, so a
/// counter there cannot reach three - and a barrier asking it to silently held
/// at nothing at all. Two machines raced that way before anybody noticed the
/// logs had no hold line in them.
///
/// Reading gt2_01 gives a later place with a name. 0x800162A0 is the phase that
/// runs the race, and it calls slot 0x44 of the object at +0x04 - the class the
/// game calls "12RaceMenuLoop", vtable 0x8002EF98. That runs a ScreenViewLoop,
/// which calls the class's slot 0x10 once a frame. So the first call to slot
/// 0x10 is the first frame of the race, and everything the overlay had to set
/// up is behind it.
///
/// Whether the first frame was late enough is a separate question, and two
/// machines racing answered it: no. The barrier released both together and the
/// frame straight after it cost 304ms on one and 487ms on the other - 183ms of
/// head start, twenty times the flight time the start line goes to such
/// trouble to remove. Loading is not what does it either; both frames read no
/// files and collected no garbage.
///
/// So the barrier moved off frame zero, and where it moved to was measured
/// rather than argued about - see HoldsAt.
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
    /// Which frame of the race the barrier holds at.
    ///
    /// Three, and the three is measured. The barrier was at frame zero, and
    /// two machines racing showed why that is too early: released together,
    /// the frame straight after cost 304ms on one and 487ms on the other, and
    /// that 183ms landed entirely in the start - twenty times the flight time
    /// the echoed token goes to such trouble to remove. Neither frame read a
    /// file or collected any garbage, so it was never loading.
    ///
    /// Nothing in the phase machine offered a later place. A run with
    /// GT2_RACE_PHASES on both machines shows two changes, both before the
    /// first frame, and then phase 9 for the whole race: the countdown has no
    /// phase change of its own. Frames were the only ruler left, and three is
    /// what a race run at three actually agreed on.
    ///
    /// Holding here means holding a little way into the countdown, where the
    /// machine that arrives first waits for the other. That is the right place
    /// for a stutter - the cars are standing still through all of it.
    /// </summary>
    const int HoldsAt = 3;

    /// <summary>
    /// GT2_START_AT_FRAME moves it, which is how the three was found and how
    /// the next one would be.
    /// </summary>
    static readonly int HoldAtFrame =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_START_AT_FRAME"), out int f)
        && f >= 0 ? f : HoldsAt;

    /// <summary>
    /// How long a frame may take before it is worth a line of its own.
    ///
    /// A race frame is meant to be about 16ms. The countdown ending is reported
    /// as three seconds of nothing, so anything near a tenth of a second is
    /// already the thing being looked for, and at that threshold a normal race
    /// says nothing at all.
    /// </summary>
    static readonly TimeSpan TooLong = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A frame to arm the read watch at, when GT2_WATCH_READ_AT names one.
    ///
    /// The race's first frame is too early for some questions. Everything the
    /// race sets up reads the cars once as it builds them, and a watch armed
    /// before that spends its whole budget on setup and never sees the
    /// per-frame readers - which, when the question is "what follows this
    /// car", are the only ones that matter.
    /// </summary>
    static readonly int WatchReadsAt =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_WATCH_READ_AT"), out int f) ? f : -1;

    static DateTime _frameBegan;
    static int _readsAtFrameStart;
    static int _stalls;
    static int _frames;
    static (int Gen0, int Gen1, int Gen2, TimeSpan Paused) _gcAtFrameStart;

    /// <summary>
    /// What the collector has done, so a stall can say whether it was the one
    /// doing it.
    ///
    /// The first measurement showed stalls of two seconds and more with zero
    /// files read during them, which rules out the disc and leaves the runtime.
    /// A recompiled game allocates in places a game does not - a context
    /// snapshot per call out of a hook, an errand per task switch - so the
    /// collector is the first thing to ask about, and asking costs three
    /// counters.
    /// </summary>
    static (int, int, int, TimeSpan) Collector() =>
        (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
         GC.GetTotalPauseDuration());

    static TimeSpan _waitedAtFrameStart;
    static long _waitsAtFrameStart;
    static long _takesAtFrameStart;
    static long _cdCommandsAtFrameStart;
    static long _cdAnswersAtFrameStart;
    static long _pumpTicksAtFrameStart;
    static long _pumpsAtFrameStart;
    static long _sweepsAtFrameStart;
    static long _rendersAtFrameStart;
    static long _renderTicksAtFrameStart;

    /// <summary>How long redrawing has cost since this frame began.</summary>
    static double RedrawMs() =>
        (RecompOne.Runtime.Host.HostWindow.RenderTicks - _renderTicksAtFrameStart)
        * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>How long pumping has cost since this frame began.</summary>
    static double PumpedMs() =>
        (RecompOne.Runtime.Runtime.PumpedTicks - _pumpTicksAtFrameStart)
        * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Pre-hook on the race screen's per-frame method, which is not the one the
    /// barrier holds at.
    ///
    /// loop__14ScreenViewLoop calls slot 0x10 once and then slots 0x14 and 0x18
    /// in a loop, so 0x8001584C - RaceMenuLoop's slot 0x10 - runs exactly once.
    /// The first version timed frames there and reported a single frame for a
    /// whole race, which is what a once-only method looks like when it is
    /// mistaken for a loop. The per-frame work is slot 0x24, reached through
    /// the generic slot 0x14, the same shape the arcade's menu screens have.
    ///
    /// Not behind a switch: a hitch that only reports itself when a variable is
    /// set is a hitch nobody reports. At 100ms a race that runs says nothing.
    /// </summary>
    public static void FrameEnds(CpuContext c, IMemory m)
    {
        var now = DateTime.UtcNow;
        int reads = LoadTrace.Reads;

        // The barrier lives here and not in FrameBegins, which is hooked on
        // RaceMenuLoop's slot 0x10 - the method that method's own note says
        // runs exactly once. A frame counter in a method that runs once never
        // counts past one, so a barrier asking for any frame but the first
        // silently held at nothing: two machines raced with no barrier at all
        // and the logs showed neither a hold nor a start line. This is slot
        // 0x24, the one the stall report counts hundreds of.
        if (WouldHold(HoldsHere, _held, _frames, HoldAtFrame))
        {
            _held = true;
            Console.Error.WriteLine(
                $"[line] {now:HH:mm:ss.fff} frame {HoldAtFrame} of the race"
                + $" - holding the room here"
                + (HoldAtFrame == HoldsAt ? "" : " (GT2_START_AT_FRAME)"));
            ModeHook.HoldAtTheLine();

            // Waiting for the other machine is not this frame's cost. Left
            // alone it would be reported as a stall of however long the room
            // took to gather, every race.
            now = DateTime.UtcNow;
            _frameBegan = now;
        }

        // Every frame, because whatever sets it does so while the race is
        // starting - a value written before that is one about to be lost.
        ReplayView.HoldTheRaceContext(m);
        RaceBlockDump.RaceIsRunning(m);

        SecondDriver.DrivePadOne(m);
        SecondDriver.CheckItStuck(m);
        CarHunt.FrameBegins(m);
        CarState.FrameBegins(m);
        CarFind.FrameBegins(m);
        RemoteCars.FrameBegins(m);
        CarSync.FrameBegins(m);

        // Every frame, because the lap counter cannot be read once the race is
        // over - see RaceResult.
        RaceResult.Watch(m);

        // And every frame because a clock runs out between two of them.
        TimedRace.Tick(m);

        // And every frame because a button is held between two of them.
        PadWatch.Tick(m);


        if (WatchReadsAt >= 0 && _frames == WatchReadsAt)
        {
            Console.Error.WriteLine($"[read] {_frames} frames into the race - arming now");
            RecompOne.Runtime.Memory.MemoryWatch.ArmReads();
        }

        var gc = Collector();
        string? whereItWas = StallWatch.WhereItWas();
        StallWatch.FrameBegins();

        if (_frames++ > 0)
        {
            var took = now - _frameBegan;
            if (took > TooLong)
                Console.Error.WriteLine(
                    $"[stall] {now:HH:mm:ss.fff} frame {_frames - 1} took {took.TotalMilliseconds:F0}ms"
                    + $"  ({reads - _readsAtFrameStart} files read,"
                    + $" gc {gc.Item1 - _gcAtFrameStart.Gen0}/{gc.Item2 - _gcAtFrameStart.Gen1}"
                    + $"/{gc.Item3 - _gcAtFrameStart.Gen2}"
                    + $" pausing {(gc.Item4 - _gcAtFrameStart.Paused).TotalMilliseconds:F0}ms,"
                    + $" baton asked {RecompOne.Runtime.Dispatch.TaskStacks.Takes - _takesAtFrameStart} times,"
                    + $" waits {RecompOne.Runtime.Dispatch.TaskStacks.Waits - _waitsAtFrameStart}"
                    + $" costing {(RecompOne.Runtime.Dispatch.TaskStacks.Waited - _waitedAtFrameStart).TotalMilliseconds:F0}ms,"
                    + $" longest one {RecompOne.Runtime.Dispatch.TaskStacks.TakeLongest().TotalMilliseconds:F0}ms,"
                    + $" cd {RecompOne.Runtime.Cdrom.CdController.Commands - _cdCommandsAtFrameStart}"
                    + $" commands answered {RecompOne.Runtime.Cdrom.CdController.Answers - _cdAnswersAtFrameStart}"
                    + $" times, last 0x{RecompOne.Runtime.Cdrom.CdController.LastCommand:X2},"
                    + $" pumped {RecompOne.Runtime.Runtime.Pumps - _pumpsAtFrameStart} times"
                    + $" ({RecompOne.Runtime.Runtime.WindowSweeps - _sweepsAtFrameStart} sweeps,"
                    + $" {RecompOne.Runtime.Host.HostWindow.Renders - _rendersAtFrameStart} redraws"
                    + $" costing {RedrawMs():F0}ms)"
                    + $" costing {PumpedMs():F0}ms,"
                    + $" {(now - _began).TotalSeconds:F1}s into the race,"
                    + $" {++_stalls} so far)"
                    + (whereItWas is null ? "" : Environment.NewLine + $"[stall]     {whereItWas}"));
        }

        _frameBegan = now;
        _readsAtFrameStart = reads;
        _gcAtFrameStart = gc;
        _waitedAtFrameStart = RecompOne.Runtime.Dispatch.TaskStacks.Waited;
        _waitsAtFrameStart = RecompOne.Runtime.Dispatch.TaskStacks.Waits;
        _takesAtFrameStart = RecompOne.Runtime.Dispatch.TaskStacks.Takes;
        _cdCommandsAtFrameStart = RecompOne.Runtime.Cdrom.CdController.Commands;
        _cdAnswersAtFrameStart = RecompOne.Runtime.Cdrom.CdController.Answers;
        _pumpTicksAtFrameStart = RecompOne.Runtime.Runtime.PumpedTicks;
        _pumpsAtFrameStart = RecompOne.Runtime.Runtime.Pumps;
        _sweepsAtFrameStart = RecompOne.Runtime.Runtime.WindowSweeps;
        _rendersAtFrameStart = RecompOne.Runtime.Host.HostWindow.Renders;
        _renderTicksAtFrameStart = RecompOne.Runtime.Host.HostWindow.RenderTicks;
    }

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

            // Whoever built this race - the arcade, or the attract demo - it
            // is installed and has not begun to change yet.
            ReplayView.SayWhatTheRaceBecame(m);

            // Again here, because the roster was empty when the block was
            // built and the question is whether it is ever filled at all or
            // only filled by menus a launch walks past.
            CourseRoster.Say(m, "at the race's first frame");

            // From here, so what a watch catches is the race writing rather
            // than the overlay setting itself up. Two attempts to recognise a
            // car by the shape of its numbers have now found code being read
            // as data; asking which function writes a known address is the
            // game answering instead of the port guessing.
            RecompOne.Runtime.Memory.MemoryWatch.Arm();
            RecompOne.Runtime.Memory.MemoryWatch.ArmReads();
        }

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

    /// <summary>
    /// Post-hook on the same per-frame method, which is after the physics has
    /// rebuilt what it derives.
    ///
    /// A rotation written before the frame does not survive it. This is where
    /// one has to go, and the two moments are kept apart so the difference can
    /// be measured rather than assumed.
    /// </summary>
    public static void FrameDone(CpuContext c, IMemory m) => RemoteCars.FrameEnds(m);

    /// <summary>Whether this held the room, which is what the log line reports.</summary>
    public static bool Held => _held;

    /// <summary>How many frames of the race have run, which the barrier keys on.</summary>
    internal static int FramesSoFar => _frames;

    /// <summary>
    /// Whether this frame is the one to hold at. Pulled out so the frame the
    /// barrier waits for can be stated once and checked without a race: the
    /// count is of frames already run, so holding at three means three frames
    /// have gone by, and holding at zero is the first frame of all.
    /// </summary>
    internal static bool WouldHold(bool holdsHere, bool alreadyHeld, int framesSoFar, int holdAt) =>
        holdsHere && !alreadyHeld && framesSoFar == holdAt;

    /// <summary>The frame the barrier holds at unless the environment moves it.</summary>
    internal static int HoldsAtFrame => HoldAtFrame;

    /// <summary>
    /// Forgets the race just run, so the next one has a first frame again.
    ///
    /// Everything here happens on frame zero - the barrier among it. A second
    /// race that kept the first one's counter would never reach frame zero,
    /// and so would never hold: every machine would start whenever it happened
    /// to finish loading.
    /// </summary>
    public static void Forget()
    {
        _frame = 0;

        // The per-frame counter too, and this is what the barrier is keyed on
        // now: a second race that kept the first one's hundreds would never
        // see the frame it is meant to hold at.
        _frames = 0;
        _stalls = 0;

        _held = false;
        _saidQuiet = false;
        CarDriving.Forget();
    }
}
