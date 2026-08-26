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

    /// <summary>
    /// Whether the grid has been written for the race now being set up, so it
    /// is written once rather than every frame - and so a second race gets its
    /// own write.
    /// </summary>
    static bool _gridApplied;

    /// <summary>
    /// Carries the room into the race being loaded.
    ///
    /// Called as the race overlay arrives, which is the one moment the block is
    /// both complete and final. Writing when it first looks complete is too
    /// early: the menu fills it again for every track the player browses, so an
    /// early write survives only in the fields the menu does not rewrite - a
    /// race with the room's number of cars and the menu's drivers in them.
    /// </summary>
    public static void ApplyRaceGrid(RecompOne.Runtime.Memory.IMemory m)
    {
        var room = _session?.Current;
        if (room == null || room.Players.Count == 0) return;

        if (!RaceGrid.TryApply(m, room.Players, _session!.PlayerName, _carCatalogue))
        {
            Console.Error.WriteLine("[grid] the race is not built yet - the room was not applied");
            return;
        }

        Console.Error.WriteLine(
            $"[grid] {room.Players.Count} player(s) put on the grid, {_session.PlayerName} driving");

        // The race has the car now, so stop replacing what the arcade loads.
        // Leaving it on would have the next visit to the menus fighting a
        // choice from a room that is no longer running.
        CarLoad.StopDriving();

        HoldForTheStart(room);
    }

    /// <summary>
    /// The host's address as it was while the lobby was still running, which
    /// is the only time anything answers for it.
    /// </summary>
    static System.Net.IPAddress? _raceHost;

    /// <summary>How long to wait for everyone before starting anyway.</summary>
    static readonly TimeSpan StartPatience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Holds the race here until everyone is ready to begin.
    ///
    /// The race overlay has loaded and nothing has drawn yet, so every machine
    /// reaches this point in the same state - which makes it the place to line
    /// them up. Players report in and the host releases them together, rather
    /// than agreeing on a clock: nobody's clock has to match anybody else's for
    /// a message to say "now".
    ///
    /// A player who never reports in cannot hold the rest forever. After
    /// <see cref="StartPatience"/> the race begins without them, which is worse
    /// for that player than for the five who were waiting.
    /// </summary>
    static void HoldForTheStart(Room room)
    {
        if (_lanSession == null || room.Players.Count < 2)
        {
            Console.Error.WriteLine(
                $"[start] no barrier: {(_lanSession == null ? "the session is gone" : $"only {room.Players.Count} player(s)")}");
            return;
        }

        bool hosting = _session!.Phase == SessionPhase.Hosting;
        var began = DateTime.UtcNow;
        var until = began + StartPatience;

        // Timed, because "the race did not start together" has three different
        // causes and the clock tells them apart: a barrier that was never
        // reached prints nothing, one that worked prints a short wait, and one
        // that gave up prints the full patience. What none of them can show is
        // the fourth - a barrier that worked and was in the wrong place, with
        // the machines drifting apart again on whatever they load afterwards.
        Console.Error.WriteLine(
            $"[start] {began:HH:mm:ss.fff} holding for {room.Players.Count} players"
            + $" as {(hosting ? "host" : "client")}");

        if (!hosting && _raceHost is null)
        {
            Console.Error.WriteLine(
                "[start] no host address was kept from the lobby - nothing to report to");
            return;
        }

        while (DateTime.UtcNow < until)
        {
            RecompOne.Runtime.Runtime.PumpHost();

            if (hosting)
            {
                _lanSession.CollectAtTheLine();
                if (_lanSession.WaitingAtTheLine >= room.Players.Count)
                {
                    // Sent more than once: a lost Go would leave that player
                    // holding until their patience runs out, racing a start
                    // everyone else has already had.
                    for (int i = 0; i < 5; i++)
                    {
                        _lanSession.SendGo();
                        Thread.Sleep(16);
                    }
                    Console.Error.WriteLine(
                        $"[start] {DateTime.UtcNow:HH:mm:ss.fff} everyone is at the line - go"
                        + $" (waited {(DateTime.UtcNow - began).TotalSeconds:F2}s)");
                    return;
                }
            }
            else if (_raceHost is { } host)
            {
                _lanSession.ReportAtTheLine(host);
                _lanSession.CollectGo();
                if (_lanSession.HostSaidGo)
                {
                    Console.Error.WriteLine(
                        $"[start] {DateTime.UtcNow:HH:mm:ss.fff} the host said go"
                        + $" (waited {(DateTime.UtcNow - began).TotalSeconds:F2}s)");
                    return;
                }
            }

            Thread.Sleep(16);
        }

        Console.Error.WriteLine(
            $"[start] {DateTime.UtcNow:HH:mm:ss.fff} gave up waiting - starting anyway"
            + $" (nobody answered in {StartPatience.TotalSeconds:F0}s)");
    }

    public static bool TryEnterLobby(RecompOne.Runtime.Context.CpuContext c,
                                     RecompOne.Runtime.Memory.IMemory m,
                                     uint entryPoint)
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

        bool racing = RunLobby();
        if (!racing) return true;

        // The game is one instruction from loading Simulation. Point it at the
        // race overlay instead and it walks into a race it never built - which
        // is what turns Start into a race rather than into a menu tour.
        var room = _session.Current;
        if (room != null) DriveTheRoomsCar(room);

        if (RaceLauncher.Enabled && room != null
            && RaceLauncher.TryPrepare(m, room.Players, _session.PlayerName, _carCatalogue))
        {
            Console.Error.WriteLine("[launch] starting the race straight from the lobby");
            c.A1 = RaceLauncher.RaceOverlayEntry;
        }

        return true;
    }

    /// <summary>
    /// Tells CarLoad which car this machine's player agreed to drive.
    ///
    /// The lobby and the arcade menus ask the same player the same question
    /// and only one answer reaches the track - the arcade's, since it is asked
    /// last. That is fine for whoever is driving the menus and wrong for
    /// everyone else, who were told a different car over the wire.
    ///
    /// The file index comes from the archive rather than the game: CarLoad
    /// needs it to tell a request already fetching the right car from one
    /// still fetching the arcade's, and asking the game that question would
    /// mean running its lookup every tick.
    /// </summary>
    static void DriveTheRoomsCar(Room room)
    {
        var mine = room.Players.FirstOrDefault(p => p.Name == _session!.PlayerName);
        if (mine is null || string.IsNullOrEmpty(mine.Car)) return;

        if (_archive is null || !_archive.TryIndexOf($"carobj/{mine.Car}.cdo.gz", out int index))
        {
            Console.Error.WriteLine($"[car] no file in the archive for {mine.Car}");
            return;
        }

        Console.Error.WriteLine($"[car] {_session!.PlayerName} is to drive {mine.Car} (file {index})");
        CarLoad.Drive(mine.Car, index);
    }

    /// <summary>
    /// Holds the game still while the lobby runs.
    ///
    /// The game only advances when a VBlank is delivered, and nothing delivers
    /// one while this loop owns the thread. Pumping the host keeps the window
    /// alive and the screens drawing.
    /// </summary>
    /// <summary>True when the lobby ended in a race rather than in the player closing it.</summary>
    static bool RunLobby()
    {
        var lastAnnounce = DateTime.UtcNow;
        bool started = false;

        try
        {
            // Exits either when a race is started or when the player closes
            // the panel with its own close button - without the latter,
            // closing the panel leaves no visible UI but keeps pumping
            // forever, freezing the game behind a window that can never be
            // dismissed.
            // A client's lobby has no Start button, so it leaves when the host
            // says the race is on - and it has to leave holding the room, or
            // there is nothing left to build the race from.
            while (_panel!.IsOpen
                   && !(started = _panel.TryConsumeStartRequest()
                        || (_session!.Phase == SessionPhase.Joined && _lanSession?.HostSaidGo == true)))
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
                    // Kept for after the lobby. Discovery forgets a host three
                    // seconds after its last announcement, and the host only
                    // announces from this loop - so by the time the race
                    // overlay has loaded and the start barrier begins, the
                    // address is already gone and a client that looked it up
                    // there would find nothing and say nothing.
                    _raceHost = hostAddress;
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

            // Told repeatedly: the players are about to leave the lobby, and a
            // single lost datagram would strand one of them in it.
            if (started && _session!.Phase == SessionPhase.Hosting)
                for (int i = 0; i < 8; i++)
                {
                    _lanSession?.CollectAtTheLine();
                    _lanSession?.SendGo();
                    Thread.Sleep(16);
                }

            // Leaving the room is right when the player closed the lobby, and
            // wrong when they started a race: the race is about to be built
            // from the room, so the room has to outlive the lobby that made
            // it. LeaveRoom also discards any pending start request, which
            // would throw away the very thing just consumed.
            if (started) return true;

            // Leave whatever room this visit ended in so the next visit
            // reopens on the room list instead of the previous visit's
            // room, players and ready flags.
            _panel.LeaveRoom();
            return false;
        }
        finally
        {
            // Everything except a race keeps the old rule, and for the old
            // reason: LeaveRoom above runs after the loop has stopped calling
            // DecideSocketAction, so nothing else drops the socket this visit
            // was using, and an exception escaping the loop would otherwise
            // leave 34719 bound for the life of the process - which on one
            // machine means no other instance can ever host again.
            //
            // A race is the exception. The players are about to need the
            // channel more than ever: it is how they agree on when to start.
            // Closing it here would mean rebuilding it mid-race, and the host's
            // port might not be free by then. Since `started` is only ever set
            // on the clean path out of the loop, an exception still frees it.
            if (!started) DropSession();
        }
    }

    /// <summary>Frees the session socket, so another instance here can host or join.</summary>
    static void DropSession()
    {
        _lanSession?.Dispose();
        _lanSession = null;
        _lanSessionRole = null;
    }
}
