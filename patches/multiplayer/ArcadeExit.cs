using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Watches the byte the arcade uses to decide where it is going.
///
/// gt2_03's entry point is the whole arcade: it runs a menu loop, then reads
/// one byte and switches on it to pick one of five exits, one of which is the
/// race. Which value means "race" is the thing worth knowing, because it is
/// the lever a lobby would pull to reach a race without the player walking the
/// menus to get there.
///
/// Read from func_80011750_gt2_03 at 0x800117A4, where it is loaded as
/// `[0x801F0000 - 0xA0C]` and compared against 5 before indexing the jump
/// table at 0x800267DC. Setting it up is entry_800141B8's job, at 0x80014400.
///
/// Off unless GT2_ARCADE_WATCH is set.
/// </summary>
public static class ArcadeExit
{
    /// <summary>The byte the switch is taken on.</summary>
    const uint Chooser = 0x801F0000u - 0xA0Cu;

    static readonly bool Watching =
        Environment.GetEnvironmentVariable("GT2_ARCADE_WATCH") is not (null or "");

    static int _last = -1;

    /// <summary>
    /// What each value was seen to lead to, as far as the disassembly says.
    /// The table is indexed by this byte, so the mapping is fixed; what is not
    /// known without watching is which value a player choosing "race" produces.
    /// </summary>
    static string Meaning(byte value) => value switch
    {
        >= 5 => "out of range - the arcade exits without loading anything",
        _ => "one of the five exits; the race is the one that reaches 0x8001184C",
    };

    /// <summary>Called once a frame.</summary>
    public static void Tick(IMemory m)
    {
        if (!Watching) return;

        byte now = m.ReadU8(Chooser);
        if (now == _last) return;
        _last = now;

        Console.Error.WriteLine(
            $"[arcade] the exit byte at 0x{Chooser:X8} is now {now} - {Meaning(now)}");
    }
}
