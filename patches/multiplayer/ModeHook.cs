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

    /// <summary>gt2_03's entry point - the arcade, which is where a race starts from.</summary>
    const uint ArcadeEntryPoint = 0x80011750u;

    /// <summary>
    /// Which overlay the arcade is, as gt2_load_overlay counts them.
    ///
    /// Its first argument is an index into the table of entry points at
    /// 0x80091174, and its second is the entry point itself. Redirecting one
    /// without the other is what a first attempt at this did: the game
    /// decompressed Simulation's bytes because the index still said Simulation,
    /// while the port switched its function map to the arcade because that is
    /// what the entry point said. The arcade's code then read Simulation's data
    /// and followed a pointer out of RAM.
    ///
    /// Read from the executable: index 2 holds 0x80011750, and index 4 holds
    /// Simulation's 0x80013628.
    /// </summary>
    const uint ArcadeOverlayIndex = 2u;

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

        var drivers = Seats.Drivers(room.Players);

        // Leader, not WatchedDriver: the latter answers "who is this viewer
        // following" and falls back to the first driver, which is the right
        // answer for a viewer and the wrong one for everybody else. Asked
        // unconditionally it made every machine rebuild the grid led by the
        // room's first player - so on each of them entrant 0 carried that
        // player's car, name and paint, and since the pad drives entrant 0,
        // every player drove a car wearing the first player's colour. That is
        // what "all four cars are the same colour" was.
        string leader = Leader(room)?.Name ?? _session!.PlayerName;

        if (!RaceGrid.TryApply(m, drivers, leader, _carCatalogue))
        {
            Console.Error.WriteLine("[grid] the race is not built yet - the room was not applied");
            return;
        }

        Console.Error.WriteLine(
            $"[grid] {drivers.Count} driver(s) put on the grid, led by {leader}");

        // The race has the car now, so stop replacing what the arcade loads.
        // Leaving it on would have the next visit to the menus fighting a
        // choice from a room that is no longer running.
        CarLoad.StopDriving();

        // Not here. This point is the race overlay arriving, and a measured run
        // puts it 14.2s before the race's first phase change - all of it
        // course, opponents and sounds loading at whatever speed each machine
        // manages. Holding here and calling it a synchronised start is what let
        // both sides report a 0.2s wait and still begin apart; that it looked
        // right was two machines happening to load at the same speed.
        //
        // RaceStartLine holds at the race's first frame instead, or RacePhases
        // at a named phase. Only one of the three can hold: two barriers would
        // be two handshakes and the session expects one.
        if (RaceStartLine.HoldsHere || RacePhases.HoldsLater) return;

        HoldForTheStart(room);
    }

    /// <summary>
    /// Holds this machine until every player in the room has reached the same
    /// point, wherever the caller has decided that point is.
    ///
    /// Separate from <see cref="ApplyRaceGrid"/> because the two want different
    /// moments. The grid has to be written while the race block is being built;
    /// the barrier has to be as late as the race allows, and everything between
    /// the two - the course, the opponents, the sounds - takes a different
    /// length of time on every machine. Holding at the earlier moment is what
    /// let two machines report a short wait and still start apart.
    /// </summary>
    public static void HoldAtTheLine()
    {
        var room = _session?.Current;
        if (room == null) return;
        HoldForTheStart(room);
    }

    /// <summary>
    /// The host's address as it was while the lobby was still running, which
    /// is the only time anything answers for it.
    /// </summary>
    static System.Net.IPAddress? _raceHost;

    /// <summary>
    /// What the race needs to talk to the other machines: the session, the
    /// host to answer when this machine is a client, and the room it is
    /// racing. Exposed rather than passed around because the code that moves
    /// cars runs from a per-frame hook, which has nowhere to be handed
    /// anything.
    /// </summary>
    public static LanSession? Wire => _lanSession;

    public static System.Net.IPAddress? HostToAnswer =>
        _session?.Phase == SessionPhase.Hosting ? null : _raceHost;

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

        // Nothing said before this line can satisfy it. The lobby's own "the
        // race is on" used to, which is how two machines both walked through
        // a barrier neither had waited at.
        _lanSession.OpenTheStartLine();

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
                        _lanSession.SendStartTheRace();
                        Thread.Sleep(16);
                    }
                    Console.Error.WriteLine(
                        $"[start] {DateTime.UtcNow:HH:mm:ss.fff} everyone is at the line - start"
                        + $" (waited {(DateTime.UtcNow - began).TotalSeconds:F2}s)");
                    return;
                }
            }
            else if (_raceHost is { } host)
            {
                _lanSession.ReportAtTheLine(host);
                _lanSession.CollectTheStart();
                if (_lanSession.HostSaidStartTheRace)
                {
                    Console.Error.WriteLine(
                        $"[start] {DateTime.UtcNow:HH:mm:ss.fff} the host said start"
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

        // The game is one instruction from loading Simulation. Point it at the
        // arcade instead and tell DirectRace what was agreed: the arcade's
        // entry point is hooked, so it skips its own menus and runs the race.
        //
        // Pointing straight at the race overlay, which is what this used to do,
        // is what does not work - the race then runs with none of the state the
        // arcade's initialisation builds, the car loader's owner among it.
        if (DirectRace.Enabled && room != null && Leader(room) is { } lead)
        {
            DirectRace.Expect(new DirectRace.Pending(
                Seats.Drivers(room.Players), lead.Name, lead.Car, room.Track, _carCatalogue,
                Watching: IsWatching(room), Laps: room.Laps, Minutes: room.Minutes));
            c.A0 = ArcadeOverlayIndex;
            c.A1 = ArcadeEntryPoint;
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
        // A viewer's entrant 0 is the driver it is watching, so that is the car
        // this machine has to have in memory - the same call, about somebody
        // else's choice.
        var mine = Leader(room);
        if (mine is null || string.IsNullOrEmpty(mine.Car)) return;

        if (_archive is null || !_archive.TryIndexOf($"carobj/{mine.Car}.cdo.gz", out int index))
        {
            Console.Error.WriteLine($"[car] no file in the archive for {mine.Car}");
            return;
        }

        Console.Error.WriteLine($"[car] {mine.Name}'s {mine.Car} is to be loaded (file {index})");
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
                        || (_session!.Phase == SessionPhase.Joined
                            && _lanSession?.HostSaidLeaveTheLobby == true)))
            {
                RecompOne.Runtime.Runtime.PumpHost();
                _discovery!.Tick();
                _session!.Tick();

                // The race just run is still being settled while the next one
                // is being arranged - see KeepSayingHowItWent for why the two
                // overlap.
                KeepSayingHowItWent();

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

            if (started)
            {
                // A race is starting, so the last one is over being talked
                // about - and the room has to survive the next one. Nobody
                // sends lobby traffic while the game is running, and three
                // seconds of that is all it takes for the room to decide
                // everybody has left.
                StopSayingHowItWent();
                _session!.HoldTheRoomTogether();
            }

            // Told repeatedly: the players are about to leave the lobby, and a
            // single lost datagram would strand one of them in it.
            if (started && _session!.Phase == SessionPhase.Hosting)
                for (int i = 0; i < 8; i++)
                {
                    _lanSession?.SendLeaveTheLobby();
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

    /// <summary>
    /// How long after a race to keep saying how it went, and keep listening.
    ///
    /// Long, and it has to be. Two machines do not finish a race at the same
    /// moment - one screen read 1:35.970 while the other read 3:43.780 - and
    /// while a machine is still racing it is not listening for results at all.
    /// A window that opened at the end of one machine's race and closed three
    /// seconds later had no overlap with the other machine's whatsoever, so the
    /// host recorded only itself and the client only itself.
    /// </summary>
    static readonly TimeSpan ResultPatience = TimeSpan.FromMinutes(5);

    /// <summary>How often to repeat it, which is often enough to survive losses.</summary>
    static readonly TimeSpan ResultEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>What this machine's driver did, while it is still worth saying.</summary>
    static RaceResult.Finish _myResult;
    static int _mySeat = -1;
    static DateTime _stopSaying;
    static DateTime _lastSaid;
    static int _reportsShown;

    /// <summary>
    /// Reads what this machine's own race ended as, says so, and leaves the
    /// lobby to go on saying it.
    ///
    /// Each machine times its own race and nobody else's. That is not a design
    /// choice so much as the only honest arrangement available: every other car
    /// on this screen was teleported here frame by frame, so when it appeared
    /// to cross the line is a fact about the network rather than about the race.
    ///
    /// Called at the moment the race overlay is replaced, which is the last
    /// instant the race's own memory is still standing - and the last moment
    /// this can be read at all.
    /// </summary>
    public static void GatherTheResults(RecompOne.Runtime.Memory.IMemory m)
    {
        if (_session?.Current is not { } room) return;

        var drivers = Seats.Drivers(room.Players);
        _mySeat = Seats.Of(room.Players, _session.PlayerName);

        // Slot 0 is the car this machine drove - every machine rotates its own
        // player there - so that is the car whose race it can report. A viewer
        // drove nobody and has nothing to say, but still has everything to hear.
        _myResult = _mySeat >= 0 ? RaceResult.Read(m, 0) : default;
        _stopSaying = DateTime.UtcNow + ResultPatience;
        _lastSaid = DateTime.MinValue;
        _reportsShown = 0;

        _lanSession?.ForgetTheResults();
        if (_mySeat >= 0 && _lanSession is null)
            RaceStandings.Show(RaceStandings.From(drivers,
                new Dictionary<byte, RaceResult.Finish> { [(byte)_mySeat] = _myResult }));

        Console.Error.WriteLine(
            $"[result] this machine finished {_myResult.Laps} lap(s) in {_myResult.Clock}"
            + $" - saying so for the next {ResultPatience.TotalMinutes:F0} minute(s)");
    }

    /// <summary>
    /// Says how this machine's race went, and listens for everyone else's, from
    /// inside the lobby loop.
    ///
    /// Here rather than in a wait at the end of a race because the machines are
    /// not together in time: the first one back sits in the lobby while the
    /// last is still on its final lap, and a result sent to a machine that is
    /// racing is a result nobody hears. Repeating it until every driver has
    /// reported costs a datagram every quarter second and needs nobody to
    /// arrive anywhere at the same moment.
    /// </summary>
    static void KeepSayingHowItWent()
    {
        if (_lanSession is null || _session?.Current is not { } room) return;

        if (DateTime.UtcNow > _stopSaying)
        {
            // Somebody never reported - a machine that crashed, or one whose
            // player closed it on the results screen. The room cannot be held
            // together for them forever, so the patience running out is what
            // lets it drop them.
            if (_session.RoomIsHeldTogether)
            {
                Console.Error.WriteLine(
                    "[result] not everyone reported - the room stops waiting for them");
                _session.LetTheRoomBreatheAgain();
            }
            return;
        }

        var drivers = Seats.Drivers(room.Players);

        if (_mySeat >= 0 && DateTime.UtcNow - _lastSaid > ResultEvery)
        {
            _lastSaid = DateTime.UtcNow;
            _lanSession.SendResult((byte)_mySeat, _myResult, HostToAnswer);
        }

        // Nothing is collected here. The lobby's own loops already keep a
        // result out of whatever they read, and CollectResults drains the
        // socket and throws away everything that is not one - which, called
        // from this loop, ate the clients' own intents. The host then stopped
        // hearing them and dropped them for going quiet, so a client could not
        // stay in the room at all.
        //
        // Redrawn only when something new arrived: the standings are a list the
        // player is reading, not a thing to rebuild sixty times a second.
        if (_lanSession.Results.Count == _reportsShown) return;
        _reportsShown = _lanSession.Results.Count;

        var standings = RaceStandings.From(drivers, _lanSession.Results);
        RaceStandings.Show(standings);

        Console.Error.WriteLine(
            $"[result] {_reportsShown} of {drivers.Count} driver(s) have reported:"
            + string.Concat(standings.Select(x =>
                $"{Environment.NewLine}[result]   {x.Place}. {x.Name}  {x.Laps} lap(s)  {x.Clock}")));

        if (_reportsShown < drivers.Count) return;

        // Everyone is back and has said how they got on, so silence means
        // something again.
        _stopSaying = DateTime.UtcNow;
        _session.LetTheRoomBreatheAgain();

        // And the next grid opens in the order this race finished. Here rather
        // than as each result lands, so the grid does not shuffle under a host
        // who is reading it.
        _session.ArrangeByTheLastRace();
    }

    /// <summary>Forgets the last race, so its result is not sent into the next.</summary>
    static void StopSayingHowItWent()
    {
        _mySeat = -1;
        _myResult = default;
        _stopSaying = DateTime.MinValue;
        _reportsShown = 0;
        _lanSession?.ForgetTheResults();
    }

    /// <summary>
    /// Whether there is a room to come back to once a race is over.
    ///
    /// A race that was never started from a lobby - the arcade's own, or one
    /// launched from a capture with no room behind it - has nothing to return
    /// to, and must be left to end where it always did.
    /// </summary>
    public static bool HasARoomToReturnTo => _panel is not null && _session?.Current is not null;

    /// <summary>
    /// Runs the lobby again, on the overlay load that follows a race.
    ///
    /// The same loop as <see cref="TryEnterLobby"/>'s, at a different moment.
    /// That one is the game asking for Simulation; this one is the arcade
    /// coming back after the race overlay has finished with it. The difference
    /// is only what happens when the lobby ends in a race: there is no overlay
    /// to redirect, because the arcade - which is what a race starts from - is
    /// the overlay already arriving.
    ///
    /// Nothing has to be rebuilt to get here. The socket is kept when a lobby
    /// ends in a race rather than dropped (see RunLobby's finally), and the
    /// room is the same object every player left. Coming back is reopening the
    /// panel over it.
    /// </summary>
    public static void ReturnToTheLobby()
    {
        if (_panel is null || _session is null) return;

        _lanSession?.ForgetTheLobbyWasLeft();
        _panel.IsOpen = true;

        Console.Error.WriteLine("[lobby] the race is over - back to the room");

        if (!RunLobby()) return;

        var room = _session.Current;
        if (room is null) return;

        DriveTheRoomsCar(room);

        if (DirectRace.Enabled && Leader(room) is { } lead)
            DirectRace.Expect(new DirectRace.Pending(
                Seats.Drivers(room.Players), lead.Name, lead.Car, room.Track, _carCatalogue,
                Watching: IsWatching(room), Laps: room.Laps, Minutes: room.Minutes));
    }

    /// <summary>Whether this machine's player is in the room to watch rather than race.</summary>
    static bool IsWatching(Room room) =>
        room.Players.FirstOrDefault(p => p.Name == _session!.PlayerName)?.Watching == true;

    /// <summary>
    /// The driver this machine's race is built around: its own player when it
    /// is racing, and the driver it is watching when it is not. Null when there
    /// is neither - a room of nothing but viewers has no race to build.
    /// </summary>
    static Player? Leader(Room room) =>
        IsWatching(room)
            ? _session!.WatchedDriver()
            : room.Players.FirstOrDefault(p => p.Name == _session!.PlayerName);

    /// <summary>Frees the session socket, so another instance here can host or join.</summary>
    static void DropSession()
    {
        _lanSession?.Dispose();
        _lanSession = null;
        _lanSessionRole = null;
    }
}
