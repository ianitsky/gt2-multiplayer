namespace GT2Port.Multiplayer;

/// <summary>
/// Where a session is. <see cref="Knocking"/> is the one that only exists
/// because of the internet: a room found by announcement arrives whole and is
/// joined at once, while a room found by address arrives as nothing at all and
/// has to be asked.
/// </summary>
public enum SessionPhase { Browsing, Hosting, Joined, Disconnected, Knocking }

/// <summary>
/// Room membership, with no sockets and no screen.
///
/// Everything that can be logically wrong about a lobby lives here, so it is
/// all reachable from tests. The clock is injected for the same reason: a
/// three second timeout should not take three seconds to test.
///
/// The host owns the room. A client only ever adopts what the host sends
/// (OnRemoteState) rather than editing its own copy, which is what keeps the
/// two from drifting apart.
/// </summary>
public sealed class Session
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    string _playerName;
    readonly Func<DateTime> _clock;
    readonly Dictionary<string, DateTime> _lastHeard = [];
    DateTime _hostLastHeard;

    /// <summary>
    /// Until when silence is not evidence that anybody has gone.
    ///
    /// Three seconds of quiet means a machine has left - except during a race,
    /// when it means a machine is racing. Nobody sends lobby traffic while the
    /// game is running, so without this the room takes itself apart the moment
    /// it is used: the client loses the room to "The host left the room", the
    /// host prunes every player who is out on track, and the players who get
    /// back first find no room to be in and have to join one again by hand.
    ///
    /// A race has no length worth guessing at - ninety-nine laps is allowed -
    /// so the hold does not expire on a timer. It is lifted when every driver
    /// has said how their race went, which is the first moment silence means
    /// something again.
    /// </summary>
    DateTime _holdUntil = DateTime.MinValue;

    /// <summary>
    /// Holds the room together across a race, however long it takes and
    /// whatever order the machines come back in.
    /// </summary>
    public void HoldTheRoomTogether() => _holdUntil = DateTime.MaxValue;

    /// <summary>
    /// Lets the room drop people again, once the race is settled.
    ///
    /// Everyone is treated as heard from at this instant. They were not silent,
    /// they were racing, and releasing the hold without saying so would drop
    /// the whole room on the very next tick.
    /// </summary>
    public void LetTheRoomBreatheAgain()
    {
        if (_holdUntil == DateTime.MinValue) return;

        _holdUntil = DateTime.MinValue;
        _hostLastHeard = _clock();

        if (Current is { } room)
            foreach (var player in room.Players)
                _lastHeard[player.Name] = _clock();
    }

    /// <summary>Whether the room is being held together across a race.</summary>
    public bool RoomIsHeldTogether => _holdUntil != DateTime.MinValue;

    public Session(string playerName, Func<DateTime> clock)
    {
        _playerName = playerName;
        _clock = clock;
    }

    public string PlayerName => _playerName;
    public SessionPhase Phase { get; private set; } = SessionPhase.Browsing;
    public Room? Current { get; private set; }
    public string? StatusMessage { get; private set; }

    /// <summary>
    /// Changes the player's name before they've joined or hosted a room.
    /// Refused once a room exists (<see cref="SessionPhase.Hosting"/> or
    /// <see cref="SessionPhase.Joined"/>) - the host's row and any client's
    /// keep-alive bookkeeping are keyed by name, so renaming mid-room would
    /// desync them from what the other side still knows this player as.
    /// </summary>
    public bool Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (Phase != SessionPhase.Browsing) return false;

        _playerName = name;
        return true;
    }

    /// <summary>
    /// Whether the host may start the race.
    ///
    /// A room of one is allowed. It is not multiplayer, but it is the same
    /// path - the same block, the same grid, the same launch - with nobody to
    /// wait for, and requiring a second player to exercise it means two
    /// machines for every test of something neither of them shares.
    /// </summary>
    public bool CanStart =>
        Phase == SessionPhase.Hosting &&
        Current is { } room &&
        room.Players.Count > 0 &&
        room.Players.All(p => p.Ready);

    /// <summary>
    /// Starts hosting with <paramref name="maxPlayers"/> clamped to
    /// 2..<see cref="RoomState.MaxPlayers"/> - one player is not multiplayer,
    /// and a caller passing an untrusted or out-of-range value cannot widen
    /// the room past the wire format's own cap.
    /// </summary>
    /// <summary>
    /// Opens a room, with the race it will run already decided.
    ///
    /// The length is settled here rather than in the lobby because it is a
    /// property of the room, like the track and the class: the lobby is where
    /// competitors sort themselves out and pick cars, and a control that
    /// changes what everybody is about to race does not belong among them.
    /// </summary>
    public void Host(string roomName, string track, string carGroup,
                     int maxPlayers = RoomState.MaxPlayers,
                     int laps = RaceLaps.AsBuilt, int minutes = TimedRace.ByLaps,
                     bool qualifying = false, string secret = "")
    {
        // Clamped here as everywhere else, because the same two numbers reach
        // this from a slider, from a socket and from a test.
        Current = new Room(
            Guid.NewGuid(), roomName, track, carGroup,
            Math.Clamp(maxPlayers, 2, RoomState.MaxPlayers),
            [new Player(_playerName, "", false)],
            (byte)RaceLaps.Sensible(laps),
            minutes <= 0 ? TimedRace.ByLaps : (ushort)TimedRace.Sensible(minutes),
            qualifying ? RoomStage.Qualifying : RoomStage.Racing,
            secret);
        Phase = SessionPhase.Hosting;
        StatusMessage = null;
        _lastHeard.Clear();
    }

    /// <summary>Where this client is knocking, and what it is saying.</summary>
    public string KnockingAt { get; private set; } = "";
    public string KnockingSecret { get; private set; } = "";

    /// <summary>
    /// Starts asking a host at a typed address to be let in.
    ///
    /// A room found by announcement arrives whole - its id, its track, its
    /// players - and <see cref="Join"/> is handed all of it. A room found by
    /// address arrives as nothing at all, so there is a phase between browsing
    /// and being in it: knocking, which is this client repeating an intent at
    /// an address and waiting to be answered with room state.
    ///
    /// Returns false when what was typed is not an address, which is worth
    /// saying before a single datagram is sent to nowhere.
    /// </summary>
    public bool Knock(string address, string secret)
    {
        if (!TryReadAddress(address, out _))
        {
            StatusMessage = $"\"{(address ?? "").Trim()}\" is not an address and a port.";
            return false;
        }

        Current = null;
        KnockingAt = address.Trim();
        KnockingSecret = secret;
        Phase = SessionPhase.Knocking;
        StatusMessage = "Knocking...";
        return true;
    }

    /// <summary>
    /// Reads "host:port", where the host may be a name or an address.
    ///
    /// Parsed here rather than where it is sent, so a typing mistake is a
    /// message on the screen instead of datagrams into the void.
    /// </summary>
    public static bool TryReadAddress(string typed, out System.Net.IPEndPoint where)
    {
        where = null!;
        var parts = (typed ?? "").Trim().Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[1], out int port) || port < 1 || port > 65535) return false;
        if (parts[0].Length == 0) return false;

        if (System.Net.IPAddress.TryParse(parts[0], out var address))
        {
            where = new System.Net.IPEndPoint(address, port);
            return true;
        }

        try
        {
            var found = System.Net.Dns.GetHostAddresses(parts[0]);
            if (found.Length == 0) return false;
            where = new System.Net.IPEndPoint(found[0], port);
            return true;
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or ArgumentException)
        {
            return false;
        }
    }

    public bool Join(Room room)
    {
        var distinctNames = room.Players.DistinctBy(p => p.Name).ToList();
        if (distinctNames.Count >= room.MaxPlayers)
        {
            StatusMessage = "That room is full.";
            return false;
        }
        if (distinctNames.Any(p => p.Name == _playerName))
        {
            StatusMessage = $"Someone named \"{_playerName}\" is already in that room - change your name to join.";
            return false;
        }

        Current = room with
        {
            MaxPlayers = Math.Min(room.MaxPlayers, RoomState.MaxPlayers),
            Players = [.. room.Players, new Player(_playerName, "", false)],
        };
        Phase = SessionPhase.Joined;
        StatusMessage = null;
        _hostLastHeard = _clock();
        return true;
    }

    public void Leave()
    {
        // The standings belong to the room that ran the race. Carrying them out
        // would show the next room a race none of its players were in.
        RaceStandings.Forget();
        Current = null;
        Phase = SessionPhase.Browsing;
        StatusMessage = null;
        _lastHeard.Clear();
    }

    public void SetReady(string playerName, bool ready) =>
        UpdatePlayer(playerName, p => p with { Ready = ready });

    /// <summary>
    /// Chooses a car, and takes the paint back to the first one.
    ///
    /// A paint is an index into the car's own list and the lists differ - a
    /// car with three paints and one with twelve share nothing but the
    /// numbering. Carrying the old index across a change would mean the
    /// player's car quietly repainting itself, or naming a paint the new car
    /// does not have.
    /// </summary>
    public void SetCar(string playerName, string car) =>
        UpdatePlayer(playerName, p => p.Car == car ? p : p with { Car = car, Colour = 0 });

    /// <summary>Chooses one of the paints the player's car comes in, by index.</summary>
    /// <summary>
    /// Sets how many laps the race will be run over.
    ///
    /// The host's alone: every other machine learns it from the room the host
    /// publishes, the same way it learns the track. Clamped here rather than
    /// trusted, because the number reaches this from a slider on one machine
    /// and from a socket on every other.
    /// </summary>
    public void SetLaps(int laps)
    {
        if (Current is not { } room) return;
        Current = room with { Laps = (byte)RaceLaps.Sensible(laps) };
    }

    /// <summary>
    /// Says the qualifying session has been run, so the room is arranging a
    /// race now.
    ///
    /// The host's, like everything the room says about itself. A room only ever
    /// moves this way: nothing puts a room back to qualifying, because the
    /// grid it produced is the thing the race is about to use.
    /// </summary>
    public void QualifyingIsOver()
    {
        if (Phase != SessionPhase.Hosting) return;
        if (Current is not { Stage: RoomStage.Qualifying } room) return;

        Current = room with { Stage = RoomStage.Racing };
    }

    /// <summary>
    /// Moves a driver up or down the grid. The host's alone, like the track and
    /// the laps: everyone else reads the order off the room the host publishes.
    /// </summary>
    public void MoveOnTheGrid(string playerName, int by)
    {
        if (Phase != SessionPhase.Hosting) return;
        if (Current is not { } room) return;

        Current = room with { Players = GridOrder.Move(room.Players, playerName, by) };
    }

    /// <summary>
    /// Lines the grid up in the order the last race finished, with the winner
    /// at the front.
    ///
    /// A default rather than a decision - the host can move anybody afterwards.
    /// Applied once, when the last driver's result arrives, because applying it
    /// as each result landed would shuffle the grid under the host while they
    /// were reading it.
    /// </summary>
    public void ArrangeByTheLastRace()
    {
        if (Phase != SessionPhase.Hosting) return;
        if (Current is not { } room) return;

        Current = room with
        {
            Players = GridOrder.ByTheLastRace(room.Players, RaceStandings.OfTheLastRace),
        };
    }

    /// <summary>
    /// Sets how long the race runs for, or asks for a lap race with zero.
    ///
    /// The host's alone, like the laps and the track. Clamped here rather than
    /// trusted, because it reaches this from a slider on one machine and from a
    /// socket on every other.
    /// </summary>
    public void SetMinutes(int minutes)
    {
        if (Current is not { } room) return;

        Current = room with
        {
            Minutes = minutes <= 0 ? TimedRace.ByLaps : (ushort)TimedRace.Sensible(minutes),
        };
    }

    public void SetColour(string playerName, byte colour) =>
        UpdatePlayer(playerName, p => p with { Colour = colour });

    /// <summary>Takes a seat in the race, or gives it up to watch instead.</summary>
    public void SetWatching(string playerName, bool watching) =>
        UpdatePlayer(playerName, p => p with { Watching = watching });

    /// <summary>
    /// Which driver this machine's viewer is following, by name.
    ///
    /// Local, and deliberately not on the wire: it changes nothing for anybody
    /// else, and a room that carried it would have every machine retransmitting
    /// a choice only one of them can act on. Empty means "the first driver",
    /// which is also what a name that has since left the room falls back to.
    /// </summary>
    public string Watching { get; private set; } = "";

    /// <summary>Follows a driver, by name.</summary>
    public void Watch(string driverName) => Watching = driverName;

    /// <summary>Whether this session's own player is in the room to watch.</summary>
    public bool IsWatching =>
        Current?.Players.FirstOrDefault(p => p.Name == _playerName)?.Watching == true;

    /// <summary>
    /// The driver a viewer's race is built around: the one they chose if that
    /// name is still driving, and the first driver otherwise.
    ///
    /// Null for a player who is racing, and that guard is the point of it. It
    /// used to answer for anybody, falling back to the first driver - so a
    /// caller that forgot to ask whether this player was watching got the first
    /// driver's name and built the grid around it. Every machine then led with
    /// the same player, entrant 0 carried that player's car and paint
    /// everywhere, and since the pad drives entrant 0 every player drove a car
    /// wearing somebody else's colour.
    /// </summary>
    public Player? WatchedDriver()
    {
        if (!IsWatching) return null;
        if (Current is not { } room) return null;
        var drivers = Seats.Drivers(room.Players);
        if (drivers.Count == 0) return null;
        return drivers.FirstOrDefault(p => p.Name == Watching) ?? drivers[0];
    }

    /// <summary>
    /// Applies a client's whole intent, received over the wire: update if
    /// the name is already in the room, add it if there is room, otherwise
    /// ignore. Host-only, and never touches the host's own row - a client
    /// cannot ready up, change car, or (via <see cref="ApplyClientLeave"/>)
    /// remove the host by sending a message that happens to carry its name.
    /// </summary>
    public void ApplyClientIntent(string name, string car, bool ready, byte colour = 0,
                                  bool watching = false)
    {
        if (Phase != SessionPhase.Hosting) return;
        if (name == _playerName) return;
        if (Current is not { } room) return;

        if (room.Players.Any(p => p.Name == name))
        {
            UpdatePlayer(name, p => p with
            {
                Car = car, Ready = ready, Colour = colour, Watching = watching,
            });
            OnHeard(name);
        }
        else if (room.Players.Count < room.MaxPlayers)
        {
            Current = room with
            {
                Players = [.. room.Players, new Player(name, car, ready, colour, watching)],
            };
            OnHeard(name);
        }
        // else: room is full - ignore, no row added and no keep-alive recorded.
    }

    /// <summary>
    /// Removes a client that announced it is leaving, and forgets its
    /// keep-alive record so an immediate rejoin under the same name is not
    /// instantly re-kicked by a stale timestamp. Host-only, and never
    /// touches the host's own row.
    /// </summary>
    public void ApplyClientLeave(string name)
    {
        if (Phase != SessionPhase.Hosting) return;
        if (name == _playerName) return;
        if (Current is not { } room) return;

        Current = room with { Players = [.. room.Players.Where(p => p.Name != name)] };
        _lastHeard.Remove(name);
    }

    /// <summary>The host's view of the room, adopted wholesale.</summary>
    public void OnRemoteState(Room room)
    {
        // The host is the authority on its own room; adopting a remote copy
        // here would let a stray or forged datagram overwrite it - including
        // the room id, which MultiplayerPanel captured as LocalRoomId when
        // the room was created, so the host would then start seeing its own
        // room show up in its own room list. Only a client ever adopts
        // remote state.
        if (Phase == SessionPhase.Hosting) return;

        if (Phase == SessionPhase.Joined && Current is { } joinedRoom)
        {
            // A datagram for a room other than the one we're in - forged,
            // stale, or crossed wires - must not replace it. Only the host-
            // side check (matching room id) was in the brief; this is the
            // client-side half of the same guard.
            if (room.Id != joinedRoom.Id) return;

            // The room we're in just filled up between its stale
            // announcement and us joining: the host's ApplyClientIntent
            // ignored our intent, but it still replies with room state as
            // it stands - a room with no row for us. Adopting it wholesale
            // would silently drop our own presence forever. Report it the
            // same way a host timeout is reported instead.
            if (!room.Players.Any(p => p.Name == _playerName))
            {
                Current = null;
                Phase = SessionPhase.Disconnected;
                StatusMessage = "The room is full.";
                return;
            }
        }

        var seenNames = new HashSet<string>();
        var deduped = new List<Player>();

        foreach (var player in room.Players)
        {
            if (seenNames.Add(player.Name))
            {
                // First occurrence: check if this is the local player with existing state
                if (player.Name == _playerName && Current is { } currentRoom)
                {
                    var existingLocal = currentRoom.Players.SingleOrDefault(p => p.Name == _playerName);
                    if (existingLocal is not null)
                    {
                        // Use the local player's existing state, not the incoming copy
                        deduped.Add(existingLocal);
                    }
                    else
                    {
                        deduped.Add(player);
                    }
                }
                else
                {
                    deduped.Add(player);
                }
            }
        }

        // Unconditional: deduped already carries the local player's existing
        // row (preserved above) even when no duplicate was collapsed, which
        // is the normal case for every real host reply. Guarding this on
        // deduped.Count != room.Players.Count skipped the assignment in
        // exactly that normal case, so the incoming copy of the local row -
        // Ready and Car as the host last knew them, not as they are now -
        // silently overwrote what the player had just set locally.
        room = room with { Players = deduped };

        Current = room;
        _hostLastHeard = _clock();
        foreach (var player in room.Players)
            if (player.Name != _playerName)
                _lastHeard[player.Name] = _clock();
    }

    public void OnHeard(string playerName) => _lastHeard[playerName] = _clock();

    /// <summary>
    /// Reports a problem that kept the player from ever getting into a room.
    /// The same transition <see cref="Tick"/> performs on a host timeout,
    /// minus the <see cref="SessionPhase.Disconnected"/> phase: the player
    /// isn't disconnected here, they never got connected.
    /// </summary>
    public void ReportProblem(string message)
    {
        Current = null;
        Phase = SessionPhase.Browsing;
        StatusMessage = message;
    }

    public void Tick()
    {
        if (Current is not { } room) return;
        if (_clock() < _holdUntil) return;

        var now = _clock();

        if (Phase == SessionPhase.Joined && now - _hostLastHeard > Timeout)
        {
            Current = null;
            Phase = SessionPhase.Disconnected;
            StatusMessage = "The host left the room.";
            return;
        }

        if (Phase != SessionPhase.Hosting) return;

        var live = room.Players
            .Where(p => p.Name == _playerName ||
                        (_lastHeard.TryGetValue(p.Name, out var heard) && now - heard <= Timeout))
            .ToList();

        if (live.Count != room.Players.Count)
            Current = room with { Players = live };

        // Names no longer in the room can't be pruned any other way: OnRemoteState
        // only ever adds/refreshes entries, so a dropped player's stale timestamp
        // would otherwise sit here forever and instantly re-kick them on reconnect.
        var currentNames = live.Select(p => p.Name).ToHashSet();
        foreach (var stale in _lastHeard.Keys.Where(name => !currentNames.Contains(name)).ToList())
            _lastHeard.Remove(stale);
    }

    void UpdatePlayer(string playerName, Func<Player, Player> change)
    {
        if (Current is not { } room) return;
        Current = room with
        {
            Players = [.. room.Players.Select(p => p.Name == playerName ? change(p) : p)],
        };
    }
}
