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

    // The archive is opened lazily - only when the picker actually asks for a
    // texture - not eagerly here, since building the panel shouldn't require
    // touching the disc at all (e.g. a session with no disc configured still
    // gets a working lobby, just with name-only cells). CourseMaps is built
    // once the lobby is entered so the panel always has one to draw from.
    //
    // _archiveAttempted tracks whether the open was tried at all, separately
    // from whether it succeeded: TryOpen returning null (no disc, no
    // GT2.VOL) does not stick through a plain "??=" on _archive itself, so
    // without this flag every one of the grid's 27 cache misses on the first
    // frame with no readable disc would retry the full .cue parse.
    static VolArchive? _archive;
    static bool _archiveAttempted;
    static CourseMaps? _courseMaps;

    // Built alongside _courseMaps, on the same first-lobby-entry trigger and
    // from the same lazily-opened _archive: the car picker needs the disc's
    // car names no more eagerly than the course grid needs its pictures.
    // CarCatalogue.Load also folds in config/car-groups.json, relative to
    // the working directory, exactly once - it is not re-read for the life
    // of the process.
    static CarInfo? _carInfo;
    static CarCatalogue? _carCatalogue;

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

    /// <summary>What to do with <see cref="_lanSession"/> for one iteration of <see cref="RunLobby"/>'s loop.</summary>
    internal enum SocketAction { Keep, RebuildAsHost, RebuildAsClient, Drop }

    /// <summary>
    /// The socket lifecycle, as a pure function of the role the current
    /// socket was built for (null if there is none) and the phase the
    /// session is in now. <see cref="RunLobby"/> needs a live ImGui context
    /// and so cannot be unit-tested itself; this is everything about the
    /// lifecycle decision that isn't ImGui-shaped, pulled out so it can be.
    /// Staying in the same phase two ticks running must come back Keep, not
    /// a rebuild - rebuilding every frame would exhaust ephemeral ports.
    /// </summary>
    internal static SocketAction DecideSocketAction(SessionPhase? currentRole, SessionPhase phase)
    {
        if (phase == SessionPhase.Hosting)
            return currentRole == SessionPhase.Hosting ? SocketAction.Keep : SocketAction.RebuildAsHost;
        if (phase == SessionPhase.Joined)
            return currentRole == SessionPhase.Joined ? SocketAction.Keep : SocketAction.RebuildAsClient;
        // Browsing and Disconnected hold no socket: drop one if there still
        // is one, otherwise there is nothing to do.
        return currentRole is null ? SocketAction.Keep : SocketAction.Drop;
    }

    public static bool TryEnterLobby(uint entryPoint)
    {
        if (entryPoint != SimulationEntryPoint) return false;

        _session ??= new Session(PlayerName, () => DateTime.UtcNow);
        _discovery ??= new LanDiscovery(DiscoveryPort, () => DateTime.UtcNow);
        _courseMaps ??= new CourseMaps(() =>
        {
            if (!_archiveAttempted)
            {
                _archiveAttempted = true;
                _archive = VolArchive.TryOpen(RecompOne.Runtime.Runtime.CdPath);
            }
            return _archive;
        });

        // Built once, here rather than lazily like CourseMaps' textures: the
        // picker needs names up front, not one car at a time on first draw.
        // Shares _archiveAttempted/_archive with the CourseMaps getter above,
        // so whichever of the two runs first is the one that actually opens
        // the disc.
        if (_carCatalogue == null)
        {
            if (!_archiveAttempted)
            {
                _archiveAttempted = true;
                _archive = VolArchive.TryOpen(RecompOne.Runtime.Runtime.CdPath);
            }
            _carInfo = CarInfo.TryLoad(_archive);
            _carCatalogue = CarCatalogue.Load(_carInfo, Path.Combine("config", "car-groups.json"));
        }

        // LanSession itself isn't built here: which factory to call depends
        // on the role (host or client), and that isn't known until the
        // player picks one from the room list. RunLobby builds it once the
        // role is known. The panel is given a getter rather than a snapshot
        // reference so it always sees whichever instance is current, even
        // as RunLobby rebuilds or drops it underneath it.
        if (_panel == null)
        {
            _panel = new MultiplayerPanel(_session, _discovery, () => _lanSession, _courseMaps, _carCatalogue);
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

        try
        {
            // Exits either when a race is started or when the player closes
            // the panel with its own close button - without the latter,
            // closing the panel leaves no visible UI but keeps pumping
            // forever, freezing the game behind a window that can never be
            // dismissed.
            while (_panel!.IsOpen && !_panel.TryConsumeStartRequest())
            {
                RecompOne.Runtime.Runtime.PumpHost();
                _discovery!.Tick();
                _session!.Tick();

                switch (DecideSocketAction(_lanSessionRole, _session.Phase))
                {
                    case SocketAction.RebuildAsHost:
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
                            // hosting on this port - binding would either
                            // fail (as here) or, with ReuseAddress, succeed
                            // and then silently receive nothing. Either way
                            // this player cannot host here, so send them
                            // back to the room list instead of leaving them
                            // in a room nobody else can ever reach.
                            _session.ReportProblem(
                                "Another instance is already hosting on this machine - join it instead.");
                        }
                        break;

                    case SocketAction.RebuildAsClient:
                        _lanSession?.Dispose();
                        _lanSession = null;
                        _lanSessionRole = null;
                        try
                        {
                            _lanSession = LanSession.ForClient(SessionPort, () => DateTime.UtcNow);
                            _lanSessionRole = SessionPhase.Joined;
                        }
                        catch (SocketException)
                        {
                            // Finding 4: symmetrical with the Hosting branch
                            // above - both fields are already cleared, so a
                            // failed build here cannot leave _lanSession
                            // pointing at a disposed object or
                            // _lanSessionRole holding a stale role.
                            _session.ReportProblem(
                                "Could not join - could not open a session socket on this machine.");
                        }
                        break;

                    case SocketAction.Drop:
                        // Browsing and Disconnected hold no socket, so the
                        // port - the host's fixed one, or a client's
                        // ephemeral one - is free for another instance on
                        // this machine.
                        _lanSession?.Dispose();
                        _lanSession = null;
                        _lanSessionRole = null;
                        break;

                    case SocketAction.Keep:
                        break;
                }

                if (_session.Phase == SessionPhase.Hosting)
                {
                    _lanSession?.HostTick(_session);
                }
                else if (_session.Phase == SessionPhase.Joined &&
                         _discovery.TryGetHostAddress(_session.Current!.Id, out var hostAddress))
                {
                    _lanSession?.ClientTick(_session, hostAddress);
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

            // Leave whatever room this visit ended in so the next visit
            // reopens on the room list instead of the previous visit's
            // room, players and ready flags. LeaveRoom also discards any
            // pending start request, so re-entering can't immediately fall
            // straight back out.
            _panel.LeaveRoom();
        }
        finally
        {
            // Finding 5: unconditional. LeaveRoom above runs after the loop
            // has already stopped calling DecideSocketAction, so nothing
            // else drops whatever socket this visit was using (e.g. closing
            // the panel while Hosting) - and without a finally, any
            // exception escaping the loop would skip this and leave 34719
            // bound for the rest of the process, which on one machine means
            // the other instance can never host again. Freeing it here
            // means another instance on this machine can host or join as
            // soon as this call returns, exception or not.
            _lanSession?.Dispose();
            _lanSession = null;
            _lanSessionRole = null;
        }
    }
}
