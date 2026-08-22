using RecompOne.Runtime.Host.Window;

namespace GT2Port.Multiplayer;

/// <summary>
/// Sends Simulation mode to the multiplayer lobby instead.
///
/// The game is never modified. When it asks to load the Simulation overlay,
/// this blocks inside the existing overlay hook and pumps the host window
/// itself: the game is frozen mid-call and never learns why. Nothing about it
/// is reentered until the lobby is finished.
/// </summary>
public static class ModeHook
{
    /// <summary>Verified by running the game, not assumed - see the plan's Task 5.</summary>
    const uint SimulationEntryPoint = 0x80013628u;   // gt2_ovr5_entrypoint0

    const int DiscoveryPort = 34718;

    static Session? _session;
    static LanDiscovery? _discovery;
    static MultiplayerPanel? _panel;

    public static string PlayerName { get; set; } = Environment.UserName;

    public static bool TryEnterLobby(uint entryPoint)
    {
        if (entryPoint != SimulationEntryPoint) return false;

        _session ??= new Session(PlayerName, () => DateTime.UtcNow);
        _discovery ??= new LanDiscovery(DiscoveryPort, () => DateTime.UtcNow);
        if (_panel == null)
        {
            _panel = new MultiplayerPanel(_session, _discovery);
            PanelManager.Register(_panel);
        }
        _panel.IsOpen = true;

        RunLobby();
        return true;
    }

    /// <summary>
    /// Holds the game still while the lobby runs.
    ///
    /// The game only advances when a VBlank is delivered, and nothing delivers
    /// one while this loop owns the thread. Pumping the host keeps the window
    /// alive and the screens drawing.
    /// </summary>
    static void RunLobby()
    {
        var lastAnnounce = DateTime.UtcNow;

        while (!_panel!.TryConsumeStartRequest())
        {
            RecompOne.Runtime.Runtime.PumpHost();
            _discovery!.Tick();
            _session!.Tick();

            if (_session.Phase == SessionPhase.Hosting &&
                DateTime.UtcNow - lastAnnounce > TimeSpan.FromSeconds(1))
            {
                _discovery.Announce(_session.Current!);
                lastAnnounce = DateTime.UtcNow;
            }

            Thread.Sleep(16);
        }

        _panel.IsOpen = false;
    }
}
