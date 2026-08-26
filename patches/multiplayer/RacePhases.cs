using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Watches the race walk through its phases, and holds every machine at one of
/// them so a race starts together.
///
/// The race is a class the game names itself: the vtable at 0x8002F000 carries
/// "14ArcadeRaceLoop" straight after its last slot. 0x80015F48 runs it by
/// calling three of those slots in turn - load at +0x0C, run at +0x10, unload
/// at +0x14 - and the run slot is a state machine. Each state calls a virtual,
/// takes the code it returns, and hands it to 0x80015FB0, which either keeps
/// the current state or moves to the one the table at 0x8002EF20 names for that
/// code. Thirteen codes, one transition per phase.
///
/// So 0x80015FB0 is the one place every phase change passes through, and a
/// pre-hook on it sees the whole race as a sequence of numbers.
///
/// That matters because of where a start barrier has to go. Holding when the
/// race overlay loads is too early: after it each machine loads its own course,
/// its own opponents and its own sounds, and they take different lengths of
/// time, so both sides report a short wait and still start apart. The barrier
/// belongs at the last phase change before the countdown - and which number
/// that is, this prints.
///
/// Two switches, so finding it costs a run rather than a rebuild:
///   GT2_RACE_PHASES  - print every phase change, with the clock and the number
///                      of files read, which is what says when loading stopped
///   GT2_START_AT_PHASE=N - hold the room at the first change to phase N
/// </summary>
public static class RacePhases
{
    static readonly bool Watching =
        Environment.GetEnvironmentVariable("GT2_RACE_PHASES") is not (null or "");

    /// <summary>The phase to hold at, or -1 for none.</summary>
    static readonly int HoldAt =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_START_AT_PHASE"), out int n) ? n : -1;

    /// <summary>
    /// Whether a phase has been named to hold at, which is what says the
    /// barrier at the overlay load should stand aside. Two barriers would be
    /// two handshakes, and the session only expects one.
    /// </summary>
    public static bool HoldsLater => HoldAt >= 0;

    /// <summary>Where the state machine keeps the phase it is in.</summary>
    const uint CurrentPhaseInScreen = 0x08u;

    static DateTime? _began;
    static bool _held;
    static int _changes;

    /// <summary>
    /// Pre-hook on the phase driver: A0 is the race loop, A1 the code the phase
    /// just finished returned. Zero means "stay where you are", which is the
    /// common case and not a change.
    /// </summary>
    public static void PhaseDecided(CpuContext c, IMemory m)
    {
        uint asked = c.A1;
        uint now = m.ReadU32(c.A0 + CurrentPhaseInScreen);

        // Nothing to say about a phase that ran and asked to stay put.
        if (asked == 0u) return;

        _began ??= DateTime.UtcNow;
        _changes++;

        if (Watching)
            Console.Error.WriteLine(
                $"[phase] {DateTime.UtcNow:HH:mm:ss.fff} +{(DateTime.UtcNow - _began.Value).TotalSeconds,6:F2}s"
                + $"  {_changes,2}. phase {now} -> {asked}"
                + $"  ({LoadTrace.Reads} files read)");

        if (HoldAt < 0 || _held || asked != (uint)HoldAt) return;

        _held = true;
        Console.Error.WriteLine($"[phase] holding the room at phase {asked}");
        ModeHook.HoldAtTheLine();
    }

    /// <summary>Forgets the race just run, so a second one holds again.</summary>
    public static void Forget()
    {
        _began = null;
        _held = false;
        _changes = 0;
    }
}
