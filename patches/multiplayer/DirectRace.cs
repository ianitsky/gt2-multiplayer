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

    /// <summary>What the lobby settled, kept because the race has to be described.</summary>
    public sealed record Pending(IReadOnlyList<Player> Players, string Me, string Car, CarCatalogue? Cars);

    static Pending? _race;

    /// <summary>Called by the lobby when a race has been agreed and is to start.</summary>
    public static void Expect(Pending race)
    {
        if (!Enabled)
        {
            Console.Error.WriteLine("[direct] a race is ready but GT2_DIRECT_LAUNCH is not set - using the menus");
            return;
        }

        _race = race;
        _armed = true;
        _armedAt = DateTime.UtcNow;
        _said = false;
        Console.Error.WriteLine(
            $"[direct] a race is waiting: {race.Players.Count} player(s), {race.Me} in {race.Car}");
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

    /// <summary>
    /// Post-hook on the pre-race screen's own "should I keep going?" method,
    /// answering zero the first time it is asked.
    ///
    /// That screen is what kills a launched race. It renders, and a pointer it
    /// renders through is wrong on this path - the writes land on a VBlank
    /// callback node at 0x801C949C, and the handler then calls whatever a
    /// primitive left where the callback was. Finding the pointer means reading
    /// the GTE-heavy drawing code, which is a long way past anything else here.
    ///
    /// It is also skippable. It lives half a second in a walked run, and the
    /// 720-byte block it appears to sit between is byte for byte identical
    /// either side of it - so it decides nothing a race needs. Ending it at
    /// once is the same trick that worked on the first screen, and it never
    /// draws a frame to go wrong in.
    /// </summary>
    /// <summary>
    /// Whether to skip the pre-race screen. On unless GT2_KEEP_PRERACE says
    /// otherwise.
    ///
    /// Letting it run was worth one try: it was first skipped for rendering
    /// through a wrong pointer, and that was found before the race block turned
    /// out not to be written at all - a screen drawing a race out of an empty
    /// block is what a wrong pointer looks like. With the block written it
    /// still dies, earlier than before and before the engine sounds load, so
    /// the fault is its own and skipping it is the better of the two.
    /// </summary>
    static readonly bool SkipPreRace =
        Environment.GetEnvironmentVariable("GT2_KEEP_PRERACE") is (null or "");

    public static void PreRaceScreenAnswered(CpuContext c, IMemory m)
    {
        if (!SkipPreRace || !EndedTheScreen || _preRaceEnded) return;
        _preRaceEnded = true;
        c.V0 = 0u;
        Console.Error.WriteLine("[direct] the pre-race screen is ended before it draws");
    }

    static bool _preRaceEnded;

    /// <summary>The pair that puts the sound sequencer on and off the VBlank list.</summary>
    const uint UnregisterSoundCallback = 0x80068708u;

    /// <summary>
    /// Takes the sound sequencer off the VBlank list before the race loads.
    ///
    /// The sequencer walks a byte stream, and once the race overlay has landed
    /// on top of the arcade's the stream is gone - it then reads a sequence
    /// byte from an address that is not memory, out of an interrupt, which is
    /// where a launched race died with everything else already right.
    ///
    /// Blunt, and worth saying so: the game has a way to stop its music that
    /// this has not found, and the pre-race screen is probably where it lives.
    /// Removing the callback stops the reading rather than stopping the music,
    /// so a launched race may be quiet until that is found.
    /// </summary>
    static void SilenceTheArcade(CpuContext c, IMemory m)
    {
        Call(c, m, UnregisterSoundCallback);
        Console.Error.WriteLine("[direct] the sound sequencer is taken off the VBlank list");
    }

    static void Call(CpuContext c, IMemory m, uint address) =>
        RecompOne.Runtime.Dispatch.Dispatcher.Call(c, m, address);

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

        // And the race itself. These two blocks describe one race between them
        // and neither is any use alone: without this one the race has no
        // description at all, and the engine sound it looks up comes out as
        // /engine/00000.es - a file whose decompression walks off the end of
        // memory. Both were captured from the same run.
        if (_race is { } race && !RaceLauncher.TryPrepare(m, race.Players, race.Me, race.Cars))
            Console.Error.WriteLine("[direct] the race block could not be written - the race will be wrong");

        SilenceTheArcade(c, m);

        // The VBlank callback list is sound here and nonsense a moment later,
        // so this is where a watch on it wants to start looking.
        RecompOne.Runtime.Memory.MemoryWatch.Arm();
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
