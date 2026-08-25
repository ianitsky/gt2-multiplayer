using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Starts a race without the menu.
///
/// The arcade menu builds a race by filling the block at 0x801D585C over
/// several screens, and until now multiplayer has ridden along with it: the
/// host picked a car and a track by hand, and the room was written over the
/// result. That works and is unbearable to play - the players have to agree on
/// a track out loud, and every one of them walks the same menus after pressing
/// Start.
///
/// So the block is supplied instead of built. A real race captured from a real
/// run is the template, the room's cars are written into it, and the game is
/// pointed at the race overlay. What the template carries beyond the fields
/// that are understood is unknown, which is the risk: it may hold something
/// belonging to the track it was captured on, and changing tracks may need
/// more than changing the name.
/// </summary>
public static class RaceLauncher
{
    const uint Block = 0x801D585Cu;
    const int BlockSize = 0x58C;

    /// <summary>gt2_01, the overlay that runs a race.</summary>
    public const uint RaceOverlayEntry = 0x80011F64u;

    static readonly string TemplatePath = Path.Combine("config", "race-template.bin");

    static byte[]? _template;

    /// <summary>
    /// Whether to launch a race straight from the lobby, off unless
    /// GT2_DIRECT_LAUNCH is set.
    ///
    /// It does not work yet. The block describes a race but does not prepare
    /// one: the race overlay runs and then reads through an object the arcade
    /// would have built on its way here, and the game dies with a black screen.
    /// Until what the arcade prepares is known, the menus are the only route
    /// that reaches a race at all, so this stays off rather than leaving no
    /// working path.
    /// </summary>
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("GT2_DIRECT_LAUNCH") is not (null or "");

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
