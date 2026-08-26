using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Starts a race from the lobby without the player walking the arcade's menus.
///
/// Everything tried here that reached past the arcade and did the work itself
/// has failed, each time on a subsystem left in a state the game never puts it
/// in. What works is the opposite: let the arcade do all of it, and change only
/// two answers.
///
///   - The arcade's first screen is a loop that asks slot 0x24 of the screen's
///     vtable every pass whether to keep going. That same method is what ticks
///     the car loader, so the game loads the room's car through its own frame
///     loop. Once the car is in memory the answer becomes zero, and the loop
///     ends the way it ends for a player: its own last pass, its own teardown,
///     back into the arcade's race case.
///
///   - That race case builds a 720-byte parameter block describing the race.
///     Built without the menus it comes out all zeroes, since it is assembled
///     from what the screens decided - the course is in it as plain text. So a
///     captured one is written over the top after the game builds it.
///
/// Everything else - the seeds, the pre-race screen, the copy, gt2_04, the race
/// overlay - is the arcade's own, untouched.
/// </summary>
public static class DirectRace
{
    /// <summary>The byte the arcade switches on when its first screen ends.</summary>
    const uint ExitByte = 0x801EF5F4u;

    /// <summary>The exit that is the race, read out of the table at 0x800267DC.</summary>
    const byte TheRace = 1;

    /// <summary>Where the arcade builds its race parameters, and how many.</summary>
    const uint Parameters = 0x801C3350u;
    const int ParametersSize = 0x2D0;

    static readonly string ParametersPath = Path.Combine("config", "race-params.bin");

    /// <summary>How long to let the car load before starting anyway.</summary>
    static readonly TimeSpan CarPatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Off unless GT2_DIRECT_LAUNCH is set. It is still untried against a path
    /// that works.
    /// </summary>
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("GT2_DIRECT_LAUNCH") is not (null or "");

    /// <summary>Whether this ended the arcade's first screen rather than the player.</summary>
    public static bool EndedTheScreen { get; private set; }

    static bool _armed;
    static DateTime _armedAt;
    static bool _endNow;
    static byte _step;
    static DateTime _lastSaid;
    static bool _said;

    /// <summary>Called by the lobby when a race has been agreed and is to start.</summary>
    public static void Expect(int players, string me, string car)
    {
        if (!Enabled)
        {
            Console.Error.WriteLine("[direct] a race is ready but GT2_DIRECT_LAUNCH is not set - using the menus");
            return;
        }

        _armed = true;
        _armedAt = DateTime.UtcNow;
        _said = false;
        Console.Error.WriteLine($"[direct] a race is waiting: {players} player(s), {me} in {car}");
    }

    /// <summary>
    /// Pre-hook on the first screen's "should I keep going?" method. Always
    /// lets it run: it is what ticks the car loader, and it is also what winds
    /// the screen down on its last pass. Only the answer changes, and that is
    /// ScreenAnswered's job.
    /// </summary>
    public static void ScreenAsks(CpuContext c, IMemory m)
    {
        if (!_armed) return;

        bool ready = CarLoad.TheRoomsCarIsLoaded(m);
        if (!ready && DateTime.UtcNow - _armedAt < CarPatience)
        {
            Waiting(m);
            return;
        }

        _armed = false;
        _endNow = true;
        EndedTheScreen = true;
        m.WriteU8(ExitByte, TheRace);

        Console.Error.WriteLine(ready
            ? "[direct] the car is loaded - letting the arcade screen finish"
            : $"[direct] the car did not load in {CarPatience.TotalSeconds:F0}s - going anyway"
              + $" (the loader stopped on step {CarLoad.StepIn(m, 0)})");
    }

    /// <summary>
    /// Post-hook on the same method. The screen has just done everything a
    /// normal last pass does; all that is left is to answer zero, which is what
    /// the loop reads to know it is over.
    ///
    /// Skipping the body instead, which an earlier version did, ends the screen
    /// without its last pass ever running - and the race then followed a
    /// display list nobody had wound down.
    /// </summary>
    public static void ScreenAnswered(CpuContext c, IMemory m)
    {
        if (!_endNow) return;
        _endNow = false;
        c.V0 = 0u;
    }

    static byte[]? _parameters;

    /// <summary>
    /// Post-hook on the arcade parameter builder. Built without the menus the
    /// block comes out all zeroes, since it is assembled from what the screens
    /// decided - the course reads out of it as text at +0xB8. So a captured one
    /// is written over the top, and the arcade copies that out as its own.
    ///
    /// The cost is worth stating plainly: every launched race runs the course
    /// the capture was taken on, whatever the room says.
    /// </summary>
    public static void ParametersBuilt(CpuContext c, IMemory m)
    {
        if (!EndedTheScreen) return;

        _parameters ??= File.Exists(ParametersPath) ? File.ReadAllBytes(ParametersPath) : null;
        if (_parameters is not { Length: >= ParametersSize })
        {
            Console.Error.WriteLine($"[direct] no race parameters at {ParametersPath}");
            return;
        }

        for (int i = 0; i < ParametersSize; i++)
            m.WriteU8(Parameters + (uint)i, _parameters[i]);
        Console.Error.WriteLine("[direct] the race parameters are supplied from a captured race");
    }

    /// <summary>
    /// Says how the wait is going, when it changes and once a second besides. A
    /// car that does not arrive is a loader step that stops advancing, and the
    /// step separates a load that never starts from one that stalls.
    /// </summary>
    static void Waiting(IMemory m)
    {
        byte step = CarLoad.StepIn(m, 0);
        var now = DateTime.UtcNow;
        if (_said && step == _step && now - _lastSaid < TimeSpan.FromSeconds(1)) return;

        _said = true;
        _step = step;
        _lastSaid = now;
        Console.Error.WriteLine(
            $"[direct] holding the arcade screen: the loader is on step {step}"
            + $" after {(now - _armedAt).TotalSeconds:F1}s");
    }
}
