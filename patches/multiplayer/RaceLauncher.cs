using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Supplies the race block instead of letting the menus build it.
///
/// The arcade builds a race by filling the block at 0x801D585C over several
/// screens, and multiplayer used to ride along with that: the host picked a car
/// and a track by hand and the room was written over the result. A real race
/// captured from a real run is the template instead, and the room's cars are
/// written into it.
///
/// What the template carries beyond the fields that are understood is unknown,
/// which is the risk: it may hold something belonging to the track it was
/// captured on, so changing tracks may need more than changing the name.
///
/// Getting the game to a race is <see cref="DirectRace"/>'s job. This only
/// decides what the race says.
/// </summary>
public static class RaceLauncher
{
    const uint Block = 0x801D585Cu;
    const int BlockSize = 0x58C;

    static readonly string TemplatePath = Path.Combine("config", "race-template.bin");

    static byte[]? _template;

    /// <summary>
    /// Writes a race into the block, ready for the overlay to pick up. False
    /// when there is no template to write, which leaves the game's own flow
    /// alone rather than starting something half-built.
    /// </summary>
    public static bool TryPrepare(IMemory m, IReadOnlyList<Player> players, string me, CarCatalogue? cars)
    {
        _template ??= File.Exists(TemplatePath) ? File.ReadAllBytes(TemplatePath) : null;
        if (_template is not { Length: >= BlockSize })
        {
            Console.Error.WriteLine($"[launch] no race template at {TemplatePath}");
            return false;
        }

        for (int i = 0; i < BlockSize; i++) m.WriteU8(Block + (uint)i, _template[i]);

        // The template's own six drivers are replaced by the room's, exactly as
        // they would have been had the menu built this.
        return RaceGrid.TryApply(m, players, me, cars);
    }
}
