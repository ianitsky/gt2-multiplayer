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
    ///
    /// GT2_REPLAY_VIEW=whole installs the captured record entire instead of
    /// changing the five fields that make one. That is the higher-fidelity
    /// answer and the riskier one - the capture names the course it was taken
    /// on and the cars that were in it, and neither is what this machine has
    /// loaded - so it is kept for comparison rather than used.
    /// </summary>
    static readonly string Force =
        Environment.GetEnvironmentVariable("GT2_REPLAY_VIEW") ?? "";

    static bool Forced => Force.Length > 0;

    static bool Wholesale => string.Equals(Force, "whole", StringComparison.OrdinalIgnoreCase);

    static byte[]? _record;

    /// <summary>Whether this race is to be shown as a replay.</summary>
    public static bool ShowsAReplay(DirectRace.Pending? race) =>
        Forced || race?.Watching == true;

    /// <summary>
    /// Turns the race the arcade has just built into the race the attract demo
    /// runs, by changing only what separates the two.
    ///
    /// Everything else in a race record is content - the race key, the course,
    /// the team names, the cars - and this machine's content is already right:
    /// the course whose geometry is loaded, and the cars whose models are in
    /// memory. Installing a captured record whole would replace all of it with
    /// somebody else's, and then have to put ours back field by field. Changing
    /// the five fields that differ cannot mismatch anything, because it does not
    /// touch anything that could.
    /// </summary>
    public static bool MakeItAReplay(IMemory m)
    {
        if (Wholesale) return InstallTheDemosRace(m);

        byte was = m.ReadU8(Block + Kind);
        m.WriteU8(Block + FirstFlag, 1);
        m.WriteU8(Block + SecondFlag, 1);
        m.WriteU8(Block + Kind, DemoKind);
        KeepItAReplay(m);

        Console.Error.WriteLine(
            $"[replay] this was a kind {was} race and is now kind {m.ReadU8(Block + Kind)},"
            + $" with every entrant marked the way a demo marks them");
        return true;
    }

    /// <summary>
    /// The two header bytes a demo race has set and an arcade race does not.
    /// What they mean is unread; that they are the only other difference in the
    /// header is measured, from two captures held side by side.
    /// </summary>
    const uint FirstFlag = 0x04u;
    const uint SecondFlag = 0x09u;

    /// <summary>The kind the attract demo's race carries.</summary>
    const byte DemoKind = 2;

    /// <summary>
    /// Where the game says whether a replay is running, according to the cheat
    /// that turns the replay cameras on in a race.
    ///
    /// That cheat gates half of itself on this address and calls 1 "replay
    /// off"; the block that undoes those patches is gated on 0 and is headed
    /// "when replay is enabled". So this is the switch the *presentation* asks,
    /// and it is not in the race record - which is why a block with the demo's
    /// kind, the demo's flags and the demo's entrant bits still came up as an
    /// ordinary race.
    ///
    /// Nothing in the game reaches it by a literal: it is a base register plus
    /// an offset in all four overlays, so reading cannot say who owns it.
    /// Holding it is the cheaper question and answers the same thing.
    /// </summary>
    const uint ReplayFlag = 0x800A92BCu;

    /// <summary>
    /// What to hold the replay flag at, when GT2_REPLAY_FLAG names a value.
    ///
    /// Held every frame rather than written once: whatever sets it up does so
    /// while the race is starting, and a value written before that is a value
    /// about to be overwritten. This is the same lesson the curtain over the
    /// arcade already carries.
    /// </summary>
    static readonly int FlagWanted =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_REPLAY_FLAG"), out int f) ? f : -1;

    static bool _saidFlag;

    /// <summary>
    /// Holds the replay flag where it was asked to be, once a frame.
    ///
    /// The first frame says what the game had it at, which is the value a race
    /// runs with and therefore the one to try the opposite of.
    /// </summary>
    public static void HoldTheReplayFlag(IMemory m)
    {
        if (!Forced) return;

        if (!_saidFlag)
        {
            _saidFlag = true;
            var around = new byte[8];
            for (uint i = 0; i < 8; i++) around[i] = m.ReadU8(ReplayFlag - 2u + i);
            Console.Error.WriteLine(
                $"[replay] the flag at 0x{ReplayFlag:X8} reads"
                + $" byte {m.ReadU8(ReplayFlag)}, halfword {m.ReadU16(ReplayFlag)},"
                + $" word 0x{m.ReadU32(ReplayFlag):X8}"
                + $" (0x{ReplayFlag - 2:X8}: " + string.Join(" ", around.Select(b => b.ToString("X2"))) + ")"
                + (FlagWanted < 0 ? " - not held" : $" - holding it at {FlagWanted}"));
        }

        if (FlagWanted < 0) return;
        m.WriteU16(ReplayFlag, (ushort)FlagWanted);
    }

    /// <summary>
    /// Puts the demo's record where the arcade left its own, keeping the course
    /// this machine has actually loaded.
    ///
    /// Only reached through GT2_REPLAY_VIEW=whole. Returns false when there is
    /// no captured record, in which case the caller carries on with the
    /// arcade's race - wrong, but not a crash.
    /// </summary>
    static bool InstallTheDemosRace(IMemory m)
    {
        _record ??= File.Exists(RecordPath) ? File.ReadAllBytes(RecordPath) : null;
        if (_record is not { Length: >= RecordSize })
        {
            Console.Error.WriteLine($"[replay] no demo race record at {RecordPath}");
            return false;
        }

        // The geometry in memory is this machine's course, not the capture's,
        // so the two fields that name it are held across the copy rather than
        // corrected afterwards - there is no moment in between when the block
        // names a course the game has not got.
        uint courseNumber = m.ReadU32(Block + CourseNumber);
        var courseName = new byte[CourseNameRoom];
        for (int i = 0; i < CourseNameRoom; i++) courseName[i] = m.ReadU8(Block + CourseName + (uint)i);

        byte was = m.ReadU8(Block + Kind);
        for (int i = 0; i < RecordSize; i++) m.WriteU8(Block + (uint)i, _record[i]);

        m.WriteU32(Block + CourseNumber, courseNumber);
        for (int i = 0; i < CourseNameRoom; i++) m.WriteU8(Block + CourseName + (uint)i, courseName[i]);

        Console.Error.WriteLine(
            $"[replay] the demo's race is installed over a kind {was} one - this is kind {m.ReadU8(Block + Kind)}");
        return true;
    }

    /// <summary>
    /// Says what the block actually holds once the race is running.
    ///
    /// This is what tells "the write never happened" from "the write happened
    /// and something put it back" from "the write stuck and a kind 2 race still
    /// looks like a race" - three failures that look identical on screen and
    /// need three different answers.
    /// </summary>
    public static void SayWhatTheRaceBecame(IMemory m)
    {
        if (!Forced) return;

        var flags = new byte[RaceGrid.Slots];
        for (uint i = 0; i < RaceGrid.Slots; i++)
            flags[i] = m.ReadU8(Block + FirstEntrant + i * EntrantSize + WhoDrives);

        Console.Error.WriteLine(
            $"[replay] at the first frame: kind {m.ReadU8(Block + Kind)},"
            + $" +0x04={m.ReadU8(Block + FirstFlag)} +0x09={m.ReadU8(Block + SecondFlag)},"
            + $" entrants " + string.Join(" ", flags.Select(f => f.ToString("X2"))));
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

}
