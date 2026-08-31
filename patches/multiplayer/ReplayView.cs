using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Asks the game, in one race, whether it will present itself as a replay
/// without being given a recording.
///
/// A viewer is meant to watch a live race the way a replay is watched. Reading
/// says where that switch is and very likely what to put in it, and stops one
/// step short of saying whether it works:
///
///   - The kind of race is one byte, at race block +0x0A. gt2_01 reads it in
///     sixty-four places and branches on values from 0 to 12. An arcade race
///     holds 4, which
///     gt2_ovr3_build_race_block_and_fill_all_six_entrants copies out of the
///     parameter block's +0x02.
///
///   - The attract demo - which is a replay, and which ran the race logic with
///     lap times counting while this port wrote car ids into its block - held
///     2.
///
///   - gt2_01 changes its own kind at runtime through the pair at 0x80017098
///     and 0x8001710C, and the kind the caller of the second one supplies is 2
///     in every path but one.
///
/// **Both were run, and both answered no.**
///
///   GT2_RACE_KIND=2 loaded the race and went straight back to the arcade menu.
///   Kind 2 is the attract demo's kind, and a race handed it without whatever
///   else the demo sets up ends itself before it draws. The game's own replay
///   is not something a viewer can be dropped into by writing one byte.
///
///   GT2_NOBODY_DRIVES=1 ran an ordinary race in which the player kept full
///   control of their car. So IsAi is not what binds a pad to a car - see
///   SecondDriver, whose third outcome this is.
///
/// Both are kept, off, because they are how the next kind or the next flag gets
/// asked the same question for the cost of a run.
/// </summary>
public static class ReplayView
{
    const uint Block = 0x801D585Cu;

    /// <summary>What kind of race this is, which is the whole question.</summary>
    const uint Kind = 0x0Au;

    const uint FirstEntrant = 0x5Cu;
    const uint EntrantSize = 0xD0u;

    /// <summary>The two RaceGrid uses to say who the game drives and how well.</summary>
    const uint AiSkill = 0x42u;
    const uint IsAi = 0x82u;

    /// <summary>
    /// A race kind to write once the race is built, when GT2_RACE_KIND names
    /// one. Two is the one worth trying; the others are what says whether 2 is
    /// special or whether any kind but 4 looks like this.
    /// </summary>
    static readonly int Wanted =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_RACE_KIND"), out int n) ? n : -1;

    /// <summary>
    /// Takes every entrant away from the pad, when GT2_NOBODY_DRIVES asks.
    ///
    /// Separate from the kind on purpose. A viewer must not be steering, and
    /// if the kind alone already stops that then this is not needed - which is
    /// a thing worth knowing rather than papering over. Run them apart first
    /// and together after.
    /// </summary>
    static readonly bool NobodyDrives =
        Environment.GetEnvironmentVariable("GT2_NOBODY_DRIVES") is not (null or "");

    /// <summary>Whether anything here is on, so a caller can say so once.</summary>
    public static bool Probing => Wanted >= 0 || NobodyDrives;

    /// <summary>
    /// Called once the race block is a finished race - after RaceGrid, so what
    /// this changes is a grid the room has already been written into.
    /// </summary>
    public static void RaceIsBuilt(IMemory m)
    {
        if (Wanted >= 0)
        {
            byte was = m.ReadU8(Block + Kind);
            m.WriteU8(Block + Kind, (byte)Wanted);
            Console.Error.WriteLine($"[replay] the race was kind {was}, now kind {Wanted}");
        }

        if (!NobodyDrives) return;

        for (uint i = 0; i < RaceGrid.Slots; i++)
        {
            uint entrant = Block + FirstEntrant + i * EntrantSize;
            m.WriteU8(entrant + IsAi, 1);
            m.WriteU8(entrant + AiSkill, 100);
        }
        Console.Error.WriteLine($"[replay] all {RaceGrid.Slots} entrants handed to the game to drive");
    }
}
