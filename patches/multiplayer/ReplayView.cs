using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Makes a viewer's race the race the attract demo runs.
///
/// A viewer is meant to see a replay, and a replay is not an ordinary race with
/// a flag flipped. Writing 2 into the kind byte at +0x0A of an arcade race
/// loaded it and walked straight back out to the arcade menu, and
/// gt2_main_func21 - the installer, at 0x80069AC4 - says why: it copies 0x58C
/// bytes into the block, then reads the kind out of what it has just copied and
/// branches on it. The kind is not a key that turns something on. It says what
/// shape the rest of the record is in.
///
/// So the record is taken whole. config/demo-race.bin is 1420 bytes captured
/// from the block while the attract demo was running one, and holding it beside
/// a captured arcade race says exactly what a replay is:
///
///   +0x04  1, where an arcade race has 0
///   +0x09  1, where an arcade race has 0
///   +0x0A  2, the kind - an arcade race is 4
///
///   every entrant's +0x82 carries 0x40, which
///   gt2_ovr3_build_race_block_and_fill_all_six_entrants sets from an argument
///   the arcade passes as zero, and entrant 0 carries 0xC0 rather than the
///   0x41 the other five do
///
/// Everything else that differs is content: the race key, the course, the team
/// names, the cars. So the demo's record goes down first and the room's own
/// content goes over the top of it, which is the same order that already makes
/// a launched arcade race work.
/// </summary>
public static class ReplayView
{
    const uint Block = 0x801D585Cu;
    const int RecordSize = 0x58C;

    /// <summary>What kind of race this is - 2 for the demo's, 4 for the arcade's.</summary>
    const uint Kind = 0x0Au;

    /// <summary>Where the block names its course, as a number and as text.</summary>
    const uint CourseName = 0x20u;
    const int CourseNameRoom = 0x20;
    const uint CourseNumber = 0x40u;

    const uint FirstEntrant = 0x5Cu;
    const uint EntrantSize = 0xD0u;

    /// <summary>
    /// Who drives an entrant, which is a set of bits rather than a flag.
    ///
    /// An arcade race writes 0 here for the car a person drives and 1 for the
    /// rest, and RaceGrid does the same. A demo writes 0xC0 on the first and
    /// 0x41 on the other five: bit 0 is still "the game drives this one", and
    /// bit 6 is on for all of them - the bit the arcade's builder sets from an
    /// argument only its replay path passes. Bit 7, on the first entrant only,
    /// is what a replay has that nothing else does.
    /// </summary>
    const uint WhoDrives = 0x82u;

    const byte DemoLeader = 0xC0;
    const byte DemoOthers = 0x41;

    static readonly string RecordPath = Path.Combine("config", "demo-race.bin");

    /// <summary>
    /// Runs a race as a replay even when this machine is racing, so the whole
    /// thing can be tried on one machine without a lobby and a second player.
    /// </summary>
    static readonly bool Forced =
        Environment.GetEnvironmentVariable("GT2_REPLAY_VIEW") is not (null or "");

    static byte[]? _record;

    /// <summary>Whether this race is to be shown as a replay.</summary>
    public static bool ShowsAReplay(DirectRace.Pending? race) =>
        Forced || race?.Watching == true;

    /// <summary>
    /// Puts the demo's record where the arcade left its own, before anything
    /// writes the room into it.
    ///
    /// Returns false when there is no captured record to install, in which case
    /// the caller carries on with the arcade's race - a viewer then sees an
    /// ordinary race, which is wrong but is not a crash.
    /// </summary>
    public static bool InstallTheDemosRace(IMemory m)
    {
        _record ??= File.Exists(RecordPath) ? File.ReadAllBytes(RecordPath) : null;
        if (_record is not { Length: >= RecordSize })
        {
            Console.Error.WriteLine($"[replay] no demo race record at {RecordPath}");
            return false;
        }

        byte was = m.ReadU8(Block + Kind);
        for (int i = 0; i < RecordSize; i++) m.WriteU8(Block + (uint)i, _record[i]);

        Console.Error.WriteLine(
            $"[replay] the demo's race is installed over a kind {was} one - this is kind {m.ReadU8(Block + Kind)}");
        return true;
    }

    /// <summary>
    /// Puts back what the room's own content overwrote.
    ///
    /// RaceGrid writes +0x82 as the arcade means it - 0 for the car a person
    /// drives and 1 for the rest - which is right for a race and wrong for a
    /// replay. This is called after it, for the same reason the paint is: the
    /// room decides who is in the race, and this decides what kind of race they
    /// are in.
    /// </summary>
    public static void KeepItAReplay(IMemory m)
    {
        for (uint i = 0; i < RaceGrid.Slots; i++)
            m.WriteU8(Block + FirstEntrant + i * EntrantSize + WhoDrives,
                      i == 0 ? DemoLeader : DemoOthers);
    }

    /// <summary>
    /// Points the installed record at the room's course.
    ///
    /// The demo's record names the course it was captured on, and the race
    /// overlay has already loaded ours - so this is not a preference, it is the
    /// block agreeing with the geometry that is in memory. The number is the
    /// same rotate-and-add hash of the asset code that the parameter block
    /// carries, and the name beside it is what the HUD reads.
    /// </summary>
    public static void PutTheRoomsCourseIn(IMemory m, string code)
    {
        if (code.Length == 0) return;

        uint id = GT2Port.Multiplayer.CourseId.Of(code);
        m.WriteU32(Block + CourseNumber, id);

        string name = CourseTable.DisplayName(code);
        for (int i = 0; i < CourseNameRoom; i++)
            m.WriteU8(Block + CourseName + (uint)i, (byte)(i < name.Length ? name[i] : 0));

        Console.Error.WriteLine($"[replay] the replay is set to {name} ({code}, 0x{id:X8})");
    }
}
