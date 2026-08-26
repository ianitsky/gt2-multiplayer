using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Ends the arcade's first screen early, so a race starts without the menus.
///
/// This began as a replacement for the arcade's entry point: skip its body and
/// replay the sequence a race needs. Five attempts, each correct in itself and
/// each revealing a new dependency somewhere else, said the approach was wrong
/// rather than unfinished. Everything the arcade does is driven by its own
/// frame loop with longjmp task switching, and replaying that synchronously
/// from a hook kept running into things that need the loop.
///
/// So nothing is replayed. The arcade runs exactly as it always has, and the
/// only intervention is to tell its first screen that it is finished:
///
///   - a screen is a loop in 0x80083418 which calls slot 0x24 of the screen's
///     vtable every pass and stops when it answers zero;
///   - for the first screen that slot is
///     gt2_ovr3_arcade_screen_tick_car_requests_and_say_whether_to_continue,
///     which is also what ticks the car loader - so the game loads the room's
///     car through its own loop and nothing here has to drive it;
///   - the byte at 0x801EF5F4 chooses which of five exits the arcade takes
///     afterwards, and 1 is the race.
///
/// Answering zero once the car is loaded, with the exit byte set, is the whole
/// of it. The arcade then seeds, builds its parameter block, copies it out and
/// loads the race overlay by itself - the sequence this used to replay.
/// </summary>
public static class DirectRace
{
    // ---- what the arcade's race case does, in the order it does it ----

    const uint Parameters = 0x801C3350u;
    const int ParametersSize = 0x2D0;

    /// <summary>Where the arcade copies the parameters on its way into the race.</summary>
    const uint ParametersGoTo = 0x801D5FA0u;

    /// <summary>Two flags the arcade raises just before the copy.</summary>
    const uint Flags = 0x801EF5F0u;

    const uint LoadOverlayDefault = 0x8005DA3Cu;
    const uint LoadOverlay = 0x8005DA7Cu;

    /// <summary>gt2_01, both as the table at 0x80091174 numbers it and by entry.</summary>
    const uint RaceOverlayIndex = 0u;
    const uint RaceOverlayEntry = 0x80011F64u;

    /// <summary>
    /// How long to let the car load before starting anyway.
    ///
    /// Starting without it is bad, but so is a lobby that never reaches a race:
    /// the arcade would sit on its first screen for ever with nothing on screen
    /// saying why.
    /// </summary>
    static readonly TimeSpan CarPatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Off unless GT2_DIRECT_LAUNCH is set. Ending a screen early is a great
    /// deal smaller than replacing the arcade, but it is still untried against
    /// a path that works.
    /// </summary>
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("GT2_DIRECT_LAUNCH") is not (null or "");

    /// <summary>Whether this ended the arcade's first screen rather than the player.</summary>
    public static bool EndedTheScreen { get; private set; }

    static bool _armed;
    static DateTime _armedAt;
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

    static byte _step;
    static DateTime _lastSaid;

    /// <summary>
    /// Says how the wait is going, when it changes and once a second besides.
    ///
    /// A car that does not arrive is a loader step that stops advancing, and
    /// the step separates the two things this could be: the load never starts,
    /// or it starts and stalls somewhere. Saying it only once - which is what
    /// this did - describes neither.
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
            $"[direct] holding the arcade's screen: the loader is on step {step}"
            + $" after {(now - _armedAt).TotalSeconds:F1}s");
    }

    /// <summary>
    /// Pre-hook on the first screen's "should I keep going?" method. Returns
    /// true to let the screen answer for itself, false to answer zero for it,
    /// which is how the screen is told it is finished.
    /// </summary>
    public static bool ScreenAsksWhetherToContinue(CpuContext c, IMemory m)
    {
        if (!_armed) return true;

        // The same method is what ticks the car loader, so letting it run is
        // how the car gets loaded at all. Ending the screen before the car is
        // in memory is the failure this whole exercise is about - and asking
        // DoneIn is how that failure happened, since it answers true for a
        // request nobody has set going yet. On the very first pass that is
        // every request there is.
        bool ready = CarLoad.TheRoomsCarIsLoaded(m);
        if (!ready && DateTime.UtcNow - _armedAt < CarPatience)
        {
            Waiting(m);
            return true;
        }

        _armed = false;
        EndedTheScreen = true;

        Console.Error.WriteLine(ready
            ? "[direct] the car is loaded - starting the race"
            : $"[direct] the car did not load in {CarPatience.TotalSeconds:F0}s - starting anyway"
              + $" (the loader stopped on step {CarLoad.StepIn(m, 0)})");

        StartTheRace(c, m);

        // Not reached: loading an overlay jumps to its entry point rather than
        // returning, which is how the arcade's own race case ends too.
        return false;
    }

    /// <summary>
    /// Does what the arcade's race case does, without its screen.
    ///
    /// The arcade builds a block of race parameters, runs a screen over the
    /// object, copies the block out and loads the race overlay. Only the last
    /// three of those prepare anything: the parameters were shown to be byte
    /// for byte identical before and after the screen, so the screen decides
    /// nothing a race needs.
    ///
    /// Letting the arcade run its own race case instead is what does not work.
    /// Its screen expects a record of the navigation that brought the player
    /// there, six menu nodes deep, and a launch that never navigated leaves
    /// that as whatever the stack held - which the screen follows into a DMA
    /// with an address that is not memory.
    ///
    /// So the preparation happens here and the race is loaded from here. This
    /// is the point the whole exercise was aiming at: one call, with everything
    /// it needs already in memory.
    /// </summary>
    static void StartTheRace(CpuContext c, IMemory m)
    {
        if (!TryWriteParameters(m)) return;

        // The two flags the arcade raises just before its copy.
        m.WriteU8(Flags + 1u, 1);
        m.WriteU8(Flags + 2u, 1);

        // Sixteen bytes at a time and then one word more, which is 0x2D4
        // rather than the 0x2D0 the loop bound suggests.
        for (uint i = 0; i < ParametersSize + 4; i += 4)
            m.WriteU32(ParametersGoTo + i, m.ReadU32(Parameters + i));

        c.A0 = 3u;
        Call(c, m, LoadOverlayDefault);

        Console.Error.WriteLine("[direct] loading the race overlay");
        c.A0 = RaceOverlayIndex;
        c.A1 = RaceOverlayEntry;
        c.A2 = 0u;
        Call(c, m, LoadOverlay);
    }

    static readonly string ParametersPath = Path.Combine("config", "race-params.bin");

    static byte[]? _parameters;

    /// <summary>
    /// Writes the race parameters from a captured race.
    ///
    /// The game builds these with 0x80010C84, and calling it here produces 720
    /// zero bytes: the block is assembled from what the arcade's first screen
    /// decided, and a launch that ends that screen early decided none of it.
    /// The block holds the course - "Tahiti Road" reads out of it in plain text
    /// at +0xB8 - so there is nothing to compute from what the room knows until
    /// the room's track can be resolved to whatever this wants.
    ///
    /// So it is supplied rather than built, the same way the race block at
    /// 0x801D585C already is. The cost is honest and worth stating: every
    /// launched race runs the course the capture was taken on, whatever the
    /// room says.
    /// </summary>
    static bool TryWriteParameters(IMemory m)
    {
        _parameters ??= File.Exists(ParametersPath) ? File.ReadAllBytes(ParametersPath) : null;
        if (_parameters is not { Length: >= ParametersSize })
        {
            Console.Error.WriteLine($"[direct] no race parameters at {ParametersPath}");
            return false;
        }

        for (int i = 0; i < ParametersSize; i++)
            m.WriteU8(Parameters + (uint)i, _parameters[i]);
        return true;
    }

    static void Call(CpuContext c, IMemory m, uint address) =>
        RecompOne.Runtime.Dispatch.Dispatcher.Call(c, m, address);
}
