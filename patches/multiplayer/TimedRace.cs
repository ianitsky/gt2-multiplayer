using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;
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
    /// <summary>
    /// The shortest and longest race the host may ask for, in minutes.
    ///
    /// One minute is far shorter than anybody would race, and is the point: the
    /// ending has to be watched to be believed, and watching it at five minutes
    /// a go costs five minutes a go.
    /// </summary>
    public const int Shortest = 1;
    public const int Longest = 180;

    /// <summary>A race that is not run to a clock at all.</summary>
    public const ushort ByLaps = 0;

    /// <summary>Clamps a length to what the host may choose.</summary>
    public static int Sensible(int minutes) => Math.Clamp(minutes, Shortest, Longest);

    static int _minutes;
    static int _began = -1;
    static bool _called;

    /// <summary>
    /// Seconds left, as the last frame worked them out.
    ///
    /// Kept rather than computed where it is shown: the clock is a number in
    /// the game's memory and the thing that draws it is handed no memory to
    /// read. One is written every frame by the code that has it, and read by
    /// the code that does not.
    /// </summary>
    static int _secondsLeft;

    static readonly Countdown Clock = new();
    static bool _registered;

    /// <summary>Whether the race now running is being run to a clock.</summary>
    public static bool Running => _minutes > 0;

    /// <summary>
    /// Starts the clock, on the first frame that finds a timed race running.
    ///
    /// Called from <see cref="Tick"/> rather than from the race's first-frame
    /// hook. That hook is 0x8001584C, which has been measured firing once in a
    /// race of 271 frames - a phase's entry rather than a frame's - and a race
    /// where it did not fire at all is a race with no clock: no countdown on
    /// the screen and, worse, nothing to ever call the last lap. The hook that
    /// runs every frame is the one that cannot be missed.
    ///
    /// The reading is kept rather than assumed to be zero, because this counter
    /// has been running since well before the race.
    /// </summary>
    static void Begins(IMemory m, int minutes)
    {
        _minutes = minutes;
        _began = minutes > 0 ? RaceResult.TicksNow(m) : -1;
        _called = false;
        _secondsLeft = minutes * 60;

        if (minutes <= 0) return;

        if (!_registered)
        {
            _registered = true;
            PanelManager.Register(Clock);
        }
        Clock.IsOpen = true;

        Console.Error.WriteLine(
            $"[timed] a {minutes} minute race - the clock reads {_began} at the first frame");
    }

    /// <summary>Forgets the race just run.</summary>
    public static void Forget()
    {
        // Before the fields, because IsOpen is what keeps LAST LAP on the
        // screen - and it stayed there through the results screens and into the
        // lobby, saying something about a race that had ended.
        Clock.IsOpen = false;

        _minutes = 0;
        _began = -1;
        _called = false;
        _secondsLeft = 0;
    }

    /// <summary>How long this race has been running, in seconds.</summary>
    public static int SecondsSoFar(IMemory m) =>
        _began < 0 ? 0 : (RaceResult.TicksNow(m) - _began) / RaceResult.PerSecond;

    /// <summary>
    /// Called once a frame. Calls the last lap when the time is up, and does
    /// nothing else ever.
    /// </summary>
    public static void Tick(IMemory m)
    {
        // Whether this race is timed is the race's own business, asked every
        // frame until the answer is acted on once.
        int wanted = DirectRace.Racing?.Minutes ?? ByLaps;
        if (wanted > ByLaps && _began < 0) Begins(m, wanted);

        if (!Running) return;

        _secondsLeft = Math.Max(0, _minutes * 60 - SecondsSoFar(m));

        if (_called) return;
        if (_secondsLeft > 0) return;

        _called = true;

        // The lap this car is on, and no more: the race should end when the
        // current lap is finished. Adding one to it was reading the field as
        // laps completed, and a car on its first lap then got a two-lap race -
        // "Lap 1/2" on the screen where it should have said 1/1.
        int onLap = RaceResult.OnLapNow(m, DirectRace.Racing?.MySlot ?? 0);
        int last = Math.Clamp(onLap, RaceLaps.Fewest, RaceLaps.Most);

        RaceLaps.CallTheLastLap(m, last);

        Console.Error.WriteLine(
            $"[timed] {_minutes} minute(s) are up after {SecondsSoFar(m)}s"
            + $" - this car is on lap {onLap}, so the race ends on lap {last}"
            + $" (0x801D5F80 reads {(int)m.ReadU32(RaceResult.Milliseconds)})");
    }

    /// <summary>
    /// How long is left, in the corner of the screen, while a timed race runs.
    ///
    /// A race against a clock is unplayable without one: a lap race tells the
    /// driver where they are on every frame - Lap 2/5 - and a timed race
    /// otherwise tells them nothing at all until it suddenly ends.
    ///
    /// Drawn by the host rather than into the game's own HUD. The game has no
    /// idea this race is timed, so there is nothing of its to add a field to,
    /// and putting one there would mean finding out how it lays a HUD out.
    /// This is a corner of the window, which the port already owns.
    /// </summary>
    sealed class Countdown : IFloatingPanel
    {
        public string Name => "Time left";
        public bool IsOpen { get; set; }

        /// <summary>How far in from the corner, so it does not touch the edge.</summary>
        static readonly Vector2 FromTheCorner = new(16f, 16f);

        /// <summary>When to start saying it in red rather than plainly.</summary>
        const int NearlyOver = 30;

        public void Draw()
        {
            var size = ImGui.GetIO().DisplaySize;
            ImGui.SetNextWindowPos(
                new Vector2(FromTheCorner.X, size.Y - FromTheCorner.Y),
                ImGuiCond.Always,
                new Vector2(0f, 1f));

            if (ImGui.Begin(Name,
                    ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize
                    | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoTitleBar
                    | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav))
            {
                if (_called)
                {
                    // The clock has run out and the race now ends when this lap
                    // does, so a countdown at zero would be saying the wrong
                    // thing rather than nothing.
                    ImGui.TextColored(new Vector4(1f, 0.55f, 0.2f, 1f), "LAST LAP");
                }
                else
                {
                    var colour = _secondsLeft <= NearlyOver
                        ? new Vector4(1f, 0.4f, 0.4f, 1f)
                        : new Vector4(1f, 1f, 1f, 1f);

                    ImGui.TextColored(colour, $"{_secondsLeft / 60}:{_secondsLeft % 60:00}");
                }
            }

            ImGui.End();
        }
    }
}
