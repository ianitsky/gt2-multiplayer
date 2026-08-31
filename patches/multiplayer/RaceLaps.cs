using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Sets how many laps a race is run over.
///
/// Which byte of the race record holds that is not yet known, and the captures
/// cannot say: every race ever captured here - two arcade, two from the attract
/// demo, and the template - was two laps, so every candidate reads 2 in all of
/// them. The header holds four bytes that do:
///
///     +0x02  +0x05  +0x08  +0x0F
///
/// and the three that differ between an arcade race and a replay - +0x04, +0x09
/// and +0x0A - are already accounted for.
///
/// Reading gt2_01 does not settle it either. The record is addressed from
/// dozens of places and nothing near any of them reads a small header offset
/// and counts with it.
///
/// So GT2_LAPS_PROBE writes a different number into each of the four and lets
/// the HUD answer: "Lap 1/7" names +0x02, "1/8" names +0x05, "1/9" names +0x08
/// and "1/11" names +0x0F. One race, and the question is closed - after which
/// GT2_LAPS_AT and GT2_LAPS confirm it without a rebuild, and the host's own
/// choice can be wired to whichever one it turns out to be.
/// </summary>
public static class RaceLaps
{
    const uint Block = 0x801D585Cu;

    /// <summary>
    /// The header bytes that read 2 in every race captured, and so could be the
    /// lap count - paired with what the probe writes into each, chosen so no
    /// two can be confused for one another on the HUD.
    /// </summary>
    static readonly (uint At, byte Probe)[] Candidates =
        [(0x02u, 7), (0x05u, 8), (0x08u, 9), (0x0Fu, 11)];

    static readonly bool Probing =
        Environment.GetEnvironmentVariable("GT2_LAPS_PROBE") is not (null or "");

    /// <summary>Which byte to write the count into, once it is known.</summary>
    static readonly uint At =
        uint.TryParse(Environment.GetEnvironmentVariable("GT2_LAPS_AT"),
                      System.Globalization.NumberStyles.HexNumber, null, out uint at) ? at : 0u;

    /// <summary>How many laps to ask for, or 0 to leave the race as it was built.</summary>
    static readonly int Wanted =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_LAPS"), out int n) ? n : 0;

    /// <summary>The range the host will be offered, and so what is accepted here.</summary>
    public const int Fewest = 1;
    public const int Most = 99;

    /// <summary>Clamps a lap count to what a race can be run over.</summary>
    public static int Sensible(int laps) => Math.Clamp(laps, Fewest, Most);

    static bool _said;

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

        if (At == 0u || Wanted <= 0) return;

        m.WriteU8(Block + At, (byte)Sensible(Wanted));
        Say(m, $"asked for {Sensible(Wanted)} lap(s) at +0x{At:X2}");
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
