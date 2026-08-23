using System.Net.Sockets;
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
    const int SessionPort = 34719;

    static Session? _session;
    static LanDiscovery? _discovery;
    static LanSession? _lanSession;

    /// <summary>
    /// Which role <see cref="_lanSession"/> was built for - null when there
    /// is none. Host and client now bind differently (see LanSession.ForHost
    /// / ForClient), so a role change means the existing instance no longer
    /// matches and must be rebuilt, not just reused.
    /// </summary>
    static SessionPhase? _lanSessionRole;

    static MultiplayerPanel? _panel;

    /// <summary>
    /// Seeds Session's initial player name. Renaming afterwards goes through
    /// Session.Rename, driven from the room-list screen - this is only ever
    /// read, once, when the lobby's Session is first constructed.
    /// </summary>
    public static string PlayerName { get; } = Environment.UserName;

    public static bool TryEnterLobby(uint entryPoint)
    {
        if (entryPoint != SimulationEntryPoint) return false;

        _session ??= new Session(PlayerName, () => DateTime.UtcNow);
        _discovery ??= new LanDiscovery(DiscoveryPort, () => DateTime.UtcNow);
        // LanSession itself isn't built here: which factory to call depends
        // on the role (host or client), and that isn't known until the
        // player picks one from the room list. RunLobby builds it once the
        // role is known. The panel is given a getter rather than a snapshot
        // reference so it always sees whichever instance is current, even
        // as RunLobby rebuilds or drops it underneath it.
        if (_panel == null)
        {
            _panel = new MultiplayerPanel(_session, _discovery, () => _lanSession);
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

        // Exits either when a race is started or when the player closes the
        // panel with its own close button - without the latter, closing the
        // panel leaves no visible UI but keeps pumping forever, freezing the
        // game behind a window that can never be dismissed.
        while (_panel!.IsOpen && !_panel.TryConsumeStartRequest())
        {
            RecompOne.Runtime.Runtime.PumpHost();
            _discovery!.Tick();
            _session!.Tick();

            if (_session.Phase == SessionPhase.Hosting)
            {
                if (_lanSessionRole != SessionPhase.Hosting)
                {
                    _lanSession?.Dispose();
                    _lanSession = null;
                    _lanSessionRole = null;
                    try
                    {
                        _lanSession = LanSession.ForHost(SessionPort, () => DateTime.UtcNow);
                        _lanSessionRole = SessionPhase.Hosting;
                    }
                    catch (SocketException)
                    {
                        // Another instance on this machine is already
                        // hosting on this port - binding would either fail
                        // (as here) or, with ReuseAddress, succeed and then
                        // silently receive nothing. Either way this player
                        // cannot host here, so send them back to the room
                        // list instead of leaving them in a room nobody
                        // else can ever reach.
                        _session.ReportProblem(
                            "Another instance is already hosting on this machine - join it instead.");
                    }
                }
                _lanSession?.HostTick(_session);
            }
            else if (_session.Phase == SessionPhase.Joined)
            {
                if (_lanSessionRole != SessionPhase.Joined)
                {
                    _lanSession?.Dispose();
                    _lanSession = LanSession.ForClient(SessionPort, () => DateTime.UtcNow);
                    _lanSessionRole = SessionPhase.Joined;
                }
                if (_discovery.TryGetHostAddress(_session.Current!.Id, out var hostAddress))
                    _lanSession.ClientTick(_session, hostAddress);
            }
            else
            {
                // Browsing and Disconnected hold no socket, so the port -
                // the host's fixed one, or a client's ephemeral one - is
                // free for another instance on this machine.
                _lanSession?.Dispose();
                _lanSession = null;
                _lanSessionRole = null;
            }

            if (_session.Phase == SessionPhase.Hosting &&
                DateTime.UtcNow - lastAnnounce > TimeSpan.FromSeconds(1))
            {
                _discovery.Announce(_session.Current!);
                lastAnnounce = DateTime.UtcNow;
            }

            Thread.Sleep(16);
        }

        _panel.IsOpen = false;

        // Leave whatever room this visit ended in so the next visit reopens
        // on the room list instead of the previous visit's room, players and
        // ready flags. LeaveRoom also discards any pending start request, so
        // re-entering can't immediately fall straight back out.
        _panel.LeaveRoom();

        // LeaveRoom runs after the loop above has already stopped checking
        // Phase, so the loop's own cleanup branch never gets a turn to drop
        // whatever socket this visit was using (e.g. closing the panel
        // while Hosting). Without this, the port stays held until the lobby
        // is reopened - freeing it here instead means another instance on
        // this machine can host or join as soon as this player leaves.
        _lanSession?.Dispose();
        _lanSession = null;
        _lanSessionRole = null;
    }
}
