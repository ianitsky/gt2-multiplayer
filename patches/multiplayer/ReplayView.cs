using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Makes a viewer's race the race the attract demo runs.
///
/// A replay is two things, in two places, and finding that out cost four wrong
/// answers worth writing down.
///
/// **The record.** Writing 2 into the kind byte at +0x0A of an arcade race
/// loaded it and walked straight back out to the menu, because
/// gt2_main_func21 - the installer, at 0x80069AC4 - copies 0x58C bytes into the
/// block and then reads the kind out of what it has just copied and branches on
/// it. The kind does not turn anything on; it says what shape the rest of the
/// record is in. Captured side by side, a demo's record and an arcade race's
/// differ in exactly five things beyond their content:
///
///   +0x04  1, where an arcade race has 0
///   +0x09  1, where an arcade race has 0
///   +0x0A  2, the kind - an arcade race is 4
///
///   every entrant's +0x82 carries 0x40, which
///   gt2_ovr3_build_race_block_and_fill_all_six_entrants sets from an argument
///   the arcade passes as zero, and entrant 0 carries 0xC0 rather than the 0x41
///   the other five do
///
/// **And the race context.** All five of those, applied and surviving to the
/// first frame, still came up as an ordinary race - so the record is not what
/// decides how a race is presented. Two frames into a demo's replay and two
/// into an arcade race, the head of the race context differs in forty bytes of
/// 1792, and thirty-six of them are inside car 0, which is position and
/// physics. What is left is <see cref="ThisIsAReplay"/> and
/// <see cref="AndSoIsThis"/>: one in a replay, zero in a race. Holding those
/// two at one, every frame, is what actually shows a replay.
///
/// Two things this is not, both measured rather than assumed. It is not the
/// address the replay-camera cheat gates on - see
/// <see cref="CheatsReplayFlag"/>. And it is not another class: gt2_01 names
/// four race loops, but a name follows its vtable rather than precedes it, so
/// 12RaceMenuLoop is 0x8002EF98 - whose slot 0x10 is this port's own
/// first-frame hook, and that hook fires during the demo. A replay and a race
/// come up through the same loop.
/// </summary>
public static class ReplayView
{
    const uint Block = 0x801D585Cu;

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

    /// <summary>
    /// Runs a race as a replay even when this machine is racing, so the whole
    /// thing can be tried on one machine without a lobby and a second player.
    ///
    /// </summary>
    static readonly bool Forced =
        Environment.GetEnvironmentVariable("GT2_REPLAY_VIEW") is not (null or "");

    /// <summary>Whether this race is to be shown as a replay.</summary>
    public static bool ShowsAReplay(DirectRace.Pending? race) =>
        !Watching && (Forced || race?.Watching == true);

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
        _forThisRace = true;

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
    /// The two bytes of the race context that a replay has set and a race does
    /// not.
    ///
    /// What they mean is unread. That they are the whole of the difference is
    /// measured: everything else that separates a demo's replay from an arcade
    /// race, two frames into each, is car 0's position and physics.
    ///
    /// 0x800A9500 is the base of the race context this port already knew from
    /// the car array at +0x188 and the car count at +0x5D2D, so a flag at +0x00
    /// and another at +0x1C are where a race would be expected to say what kind
    /// of thing it is.
    /// </summary>
    const uint ThisIsAReplay = 0x800A9500u;
    const uint AndSoIsThis = 0x800A951Cu;

    /// <summary>
    /// The address the replay-camera cheat gates itself on, kept for the
    /// record: it reads zero on an ordinary race and zero on the attract
    /// demo's replay, so on this build it is not the switch. The likely reason
    /// is the disc - a Combined Disc merges Arcade and Simulation by patching
    /// the boot executable, and a cheat's RAM address for the official 1.1 and
    /// 1.2 need not survive that. The overlay patches did, because gt2_01 is
    /// the same binary either way.
    /// </summary>
    const uint CheatsReplayFlag = 0x800A92BCu;


    /// <summary>
    /// What to hold the replay flag at, when GT2_REPLAY_FLAG names a value.
    ///
    /// Held every frame rather than written once: whatever sets it up does so
    /// while the race is starting, and a value written before that is a value
    /// about to be overwritten. This is the same lesson the curtain over the
    /// arcade already carries.
    /// </summary>
    static readonly (uint At, byte Value)[] Override = ParseHeld(
        Environment.GetEnvironmentVariable("GT2_REPLAY_HOLD") ?? "");

    /// <summary>
    /// What a replay holds, and what GT2_REPLAY_HOLD replaces when the next
    /// question about this needs asking without a build.
    /// </summary>
    static (uint At, byte Value)[] Held =>
        Override.Length > 0 ? Override : [(ThisIsAReplay, 1), (AndSoIsThis, 1)];

    /// <summary>
    /// Reads a list like <c>800A9500=1,800A951C=1</c>: an address in hex, a
    /// byte in decimal. A list rather than one address because the candidates
    /// come in combinations, and trying a combination should not need a build.
    /// </summary>
    static (uint, byte)[] ParseHeld(string spec)
    {
        var found = new List<(uint, byte)>();
        foreach (var pair in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var halves = pair.Split('=', 2);
            if (halves.Length != 2) continue;
            if (!uint.TryParse(halves[0].Trim().Replace("0x", ""),
                    System.Globalization.NumberStyles.HexNumber, null, out uint at)) continue;
            if (!byte.TryParse(halves[1].Trim(), out byte value)) continue;
            found.Add((at, value));
        }
        return [.. found];
    }

    /// <summary>
    /// What the context diff left standing, and so what a report should show.
    ///
    /// Two frames into a demo's replay and two into an arcade race, the head of
    /// the race context differs in forty bytes - and all but four of them are
    /// inside car 0, which is position and physics. What is left is 0x800A9500
    /// and 0x800A951C, one in a replay and zero in a race, and the two bytes at
    /// 0x800A9524 that look like the low half of a pointer.
    /// </summary>
    static readonly uint[] Suspects = [ThisIsAReplay, AndSoIsThis];

    /// <summary>
    /// Reports the replay flag without changing anything, so the value a real
    /// replay runs with can be measured rather than guessed at.
    ///
    /// The attract demo is a replay, and it reaches the race overlay through
    /// the same hooks this port already has - so booting and touching nothing
    /// is a measurement.
    /// </summary>
    static readonly bool Watching =
        Environment.GetEnvironmentVariable("GT2_REPLAY_WATCH") is not (null or "");

    static bool _saidFlag;

    /// <summary>
    /// Holds the replay flag where it was asked to be, once a frame.
    ///
    /// The first frame says what the game had it at, which is the value a race
    /// runs with and therefore the one to try the opposite of.
    /// </summary>
    /// <summary>
    /// Whether the race now running is one this port made a replay, so the
    /// per-frame hold knows to run without being told again.
    /// </summary>
    static bool _forThisRace;

    public static void HoldTheRaceContext(IMemory m)
    {
        if (!Forced && !Watching && !_forThisRace) return;

        if (!_saidFlag)
        {
            _saidFlag = true;
            Console.Error.WriteLine(
                "[replay] the race context reads "
                + string.Join("  ", Suspects.Select(a => $"0x{a:X8}={m.ReadU8(a)}"))
                + (Held.Length == 0
                    ? " - holding nothing"
                    : " - holding " + string.Join(",", Held.Select(h => $"0x{h.At:X8}={h.Value}"))));
        }

        // Every frame. The race sets these up while it is starting, so a value
        // written before that is a value about to be lost.
        if (Watching || !Forced && !_forThisRace) return;
        foreach (var (at, value) in Held) m.WriteU8(at, value);
    }

    /// <summary>
    /// Says what the record ended up as, once, at the race's first frame.
    ///
    /// The five fields are written while the arcade is still building, which is
    /// a long way before anything draws. This is what says they were still
    /// there when the race began - and it is what said, when they were, that
    /// the record alone changes nothing.
    /// </summary>
    public static void SayWhatTheRaceBecame(IMemory m)
    {
        if (!Forced && !_forThisRace) return;

        var who = new byte[RaceGrid.Slots];
        for (uint i = 0; i < RaceGrid.Slots; i++)
            who[i] = m.ReadU8(Block + FirstEntrant + i * EntrantSize + WhoDrives);

        Console.Error.WriteLine(
            $"[replay] at the first frame: kind {m.ReadU8(Block + Kind)},"
            + $" +0x04={m.ReadU8(Block + FirstFlag)} +0x09={m.ReadU8(Block + SecondFlag)},"
            + " entrants " + string.Join(" ", who.Select(b => b.ToString("X2"))));
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
