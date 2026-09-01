using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Sets how many laps a race is run over.
///
/// The count is the byte at <see cref="Laps"/>, and the game was asked rather
/// than guessed. No capture could name it: every race captured here - two
/// arcade, two from the attract demo, and the template - was two laps, so all
/// four header bytes that could be it read 2 in all five. Reading gt2_01 did
/// not settle it either; the record is addressed from dozens of places and none
/// of them reads a small header offset and counts with it.
///
/// So each of the four candidates - +0x02, +0x05, +0x08, +0x0F - was written a
/// different number and the lap counter was read off the screen. It said
/// "Lap 1/11", which is +0x0F.
///
/// One byte, so ninety-nine laps fits with room to spare, and the probe is kept
/// behind GT2_LAPS_PROBE: it is what would name the byte again if a different
/// build of the game moves it.
/// </summary>
public static class RaceLaps
{
    const uint Block = 0x801D585Cu;

    /// <summary>How many laps the race is run over.</summary>
    public const uint Laps = 0x0Fu;

    /// <summary>
    /// What an arcade race is built as, and so what a room that has not chosen
    /// runs: the same race the game would have run anyway.
    /// </summary>
    public const byte AsBuilt = 2;

    /// <summary>
    /// The four header bytes that read 2 in every race captured, and so could
    /// each have been the lap count - paired with what the probe writes into
    /// them, chosen so no two can be confused on the HUD. +0x0F is the answer;
    /// the other three are kept so the question can be asked again.
    /// </summary>
    static readonly (uint At, byte Probe)[] Candidates =
        [(0x02u, 7), (0x05u, 8), (0x08u, 9), (Laps, 11)];

    static readonly bool Probing =
        Environment.GetEnvironmentVariable("GT2_LAPS_PROBE") is not (null or "");

    /// <summary>
    /// Which byte to write the count into. GT2_LAPS_AT overrides it, which is
    /// what asked the question a second time without a rebuild.
    /// </summary>
    static readonly uint At =
        uint.TryParse(Environment.GetEnvironmentVariable("GT2_LAPS_AT"),
                      System.Globalization.NumberStyles.HexNumber, null, out uint at) ? at : Laps;

    /// <summary>
    /// How many laps to ask for whatever the room says, or 0 to let the room
    /// decide - which is what every ordinary run does.
    /// </summary>
    static readonly int Forced =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_LAPS"), out int n) ? n : 0;

    /// <summary>The range the host will be offered, and so what is accepted here.</summary>
    public const int Fewest = 1;
    public const int Most = 99;

    /// <summary>Clamps a lap count to what a race can be run over.</summary>
    public static int Sensible(int laps) => Math.Clamp(laps, Fewest, Most);

    static bool _said;

    /// <summary>
    /// Changes how many laps the race is, while it is being run.
    ///
    /// Only a timed race does this, and only once: when the clock runs out it
    /// sets the count to the lap the car is on, so the game ends the race the
    /// way it ends any other. Whether the game reads this byte again after the
    /// race has started, or copies it somewhere at setup, is exactly what one
    /// timed race will say - so this reports what it wrote and what the byte
    /// reads back.
    /// </summary>
    public static void CallTheLastLap(IMemory m, int laps)
    {
        m.WriteU8(Block + Laps, (byte)Sensible(laps));
        Console.Error.WriteLine(
            $"[laps] the last lap is called: +0x{Laps:X2} was set to {Sensible(laps)}"
            + $" and reads back {m.ReadU8(Block + Laps)}");
    }

    /// <summary>
    /// Called once the record is built and final, which is the same moment the
    /// room is written over it.
    /// </summary>
    public static void PutTheLapsIn(IMemory m)
    {
        if (Probing)
        {
            foreach (var (at, probe) in Candidates) m.WriteU8(Block + at, probe);
            Say(m, "probing - the HUD names the one it reads");
            return;
        }

        // A timed race starts with as many laps as the byte will hold, so the
        // game has no reason to end it before the clock does - see TimedRace.
        if (DirectRace.Racing?.Minutes > TimedRace.ByLaps)
        {
            m.WriteU8(Block + At, Most);
            Say(m, $"a {DirectRace.Racing.Minutes} minute race, so {Most} laps until the clock says otherwise");
            return;
        }

        // The room's, unless a run has been told otherwise. A race launched
        // with no room at all keeps what the arcade built.
        int wanted = Forced > 0 ? Forced : DirectRace.Racing?.Laps ?? 0;
        if (wanted <= 0) return;

        m.WriteU8(Block + At, (byte)Sensible(wanted));
        Say(m, $"{Sensible(wanted)} lap(s)"
             + (Forced > 0 ? " as asked for by GT2_LAPS" : " as the room agreed"));
    }

    static void Say(IMemory m, string what)
    {
        if (_said) return;
        _said = true;

        Console.Error.WriteLine(
            $"[laps] {what} - the header now reads "
            + string.Join("  ", Candidates.Select(x => $"+0x{x.At:X2}={m.ReadU8(Block + x.At)}")));
    }

    /// <summary>Forgets the race just run, so the next one says what it did.</summary>
    public static void Forget() => _said = false;
}
