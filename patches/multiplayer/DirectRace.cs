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
    /// <summary>The byte the arcade switches on when its first screen ends.</summary>
    const uint ExitByte = 0x801EF5F4u;

    /// <summary>The exit that is the race, read out of the table at 0x800267DC.</summary>
    const byte TheRace = 1;

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
        m.WriteU8(ExitByte, TheRace);

        // Answered rather than returned: a false from the hook only skips the
        // body, and what the screen's loop inspects is V0.
        c.V0 = 0u;

        Console.Error.WriteLine(ready
            ? "[direct] the car is loaded - ending the arcade's screen for the race"
            : $"[direct] the car did not load in {CarPatience.TotalSeconds:F0}s - starting anyway"
              + $" (the loader stopped on step {CarLoad.StepIn(m, 0)})");
        return false;
    }
}
