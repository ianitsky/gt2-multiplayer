using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// What a race ended as, for the machine that ran it: how many laps its driver
/// completed and how long the race took.
///
/// Both come from the game rather than being timed here, which is the point.
/// A port that ran its own stopwatch would be timing the wall clock while the
/// game timed the race, and the two disagree by every frame the game spent
/// loading, paused, or running slow.
///
/// **Laps** is the short at <see cref="Laps"/> of a car. It was found by asking
/// which fields move a handful of times in a whole race rather than every
/// frame, and confirmed across two races that differed: 3 after three laps, 2
/// after two, never falling.
///
/// **The time** took longer, and three searches failed before a fourth found
/// it. Counting rises and falls over halfwords finds no clock at all, because a
/// 32-bit counter's low half wraps every 65536 and that reads as a fall.
/// Counting words found two - 0x800A8D72, which counts frames exactly, and
/// 0x800A8C64, which counts two per frame - and neither is the race time: both
/// were already running before the lights went green. Which is also why the
/// search could never have worked, since a clock reset at the green light falls
/// once. And it is nowhere in the race's own 64 kilobytes, in any unit tried.
///
/// So the whole of RAM was kept at the moment the race overlay was replaced,
/// and searched for the number the screen had shown. A race that ended at
/// 2:21.456 held **141456** - milliseconds, exactly - in three places, and the
/// three are not equally believable:
///
///   0x801D5F80   0x198 past the end of the race record at 0x801D585C, among
///                entries of five words on a 0x14 stride reading
///                -1 -1 -1 -1 -65536, which is what an unset time looks like
///   0x8005AC80   inside a repeating 0x20-byte structure of large constants
///   0x801B75D4   surrounded by values in the hundreds of millions
///
/// The first is the one that looks like a race result and the other two look
/// like data that happens to contain the number. That is a good argument and
/// not a measurement, so all three are read and reported until a race says
/// which of them tracks the screen. Naming the right one then costs nothing.
/// </summary>
public static class RaceResult
{
    /// <summary>How many laps this car has completed.</summary>
    public const uint Laps = 0x634u;

    /// <summary>
    /// The race time, in milliseconds.
    ///
    /// Chosen from the three addresses that held it rather than proven to be
    /// it - see the account above. GT2_RACE_TIME_AT moves it, so if a race
    /// shows one of the others tracking the screen instead, saying so costs a
    /// run rather than a build.
    /// </summary>
    public static readonly uint Milliseconds =
        uint.TryParse(Environment.GetEnvironmentVariable("GT2_RACE_TIME_AT"),
                      System.Globalization.NumberStyles.HexNumber, null, out uint at)
            ? at : 0x801D5F80u;

    /// <summary>
    /// The other two, kept and printed beside it. They cost one line a race and
    /// they are what would name the right one the moment this one is seen to
    /// disagree with the screen.
    /// </summary>
    static readonly uint[] AlsoHeldIt = [0x8005AC80u, 0x801B75D4u];

    /// <summary>What this machine's driver did, as the game recorded it.</summary>
    public readonly record struct Finish(int Laps, int Milliseconds)
    {
        /// <summary>The time as the game would show it: m:ss.mmm.</summary>
        public string Clock =>
            Milliseconds <= 0 ? "--:--.---"
            : $"{Milliseconds / 60000}:{Milliseconds % 60000 / 1000:00}.{Milliseconds % 1000:000}";
    }

    /// <summary>
    /// Reads what the race just ended as, for the car in the given slot.
    ///
    /// Called at the moment the race overlay is replaced, which is the last
    /// instant the race's own memory is still standing.
    /// </summary>
    public static Finish Read(IMemory m, int slot)
    {
        int laps = (short)m.ReadU16(
            RemoteCars.FirstCarObject + (uint)(slot * RemoteCars.CarStride) + Laps);

        return new Finish(laps, (int)m.ReadU32(Milliseconds));
    }

    /// <summary>
    /// Says what every candidate held, so the next race names the real one.
    ///
    /// Printed beside the laps, because the two together are checkable against
    /// a screen anybody can read: a two-lap race that took 2:21.456 should say
    /// 2 laps and 2:21.456, and whichever address does not is not the time.
    /// </summary>
    public static void Say(IMemory m, int slot)
    {
        var finish = Read(m, slot);
        Console.Error.WriteLine(
            $"[result] car {slot} finished {finish.Laps} lap(s) in {finish.Clock}"
            + " - the two that also held it read "
            + string.Join("  ", AlsoHeldIt.Select(a => $"0x{a:X8}={AsATime(m, a)}")));
    }

    static string AsATime(IMemory m, uint at)
    {
        int ms = (int)m.ReadU32(at);
        return ms <= 0 || ms > 60 * 60 * 1000
            ? ms.ToString()
            : $"{ms} ({new Finish(0, ms).Clock})";
    }
}
