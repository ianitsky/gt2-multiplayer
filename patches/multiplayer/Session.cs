namespace GT2Port.Multiplayer;

public enum SessionPhase { Browsing, Hosting, Joined, Disconnected }

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

    readonly string _playerName;
    readonly Func<DateTime> _clock;
    readonly Dictionary<string, DateTime> _lastHeard = [];
    DateTime _hostLastHeard;

    public Session(string playerName, Func<DateTime> clock)
    {
        _playerName = playerName;
        _clock = clock;
    }

    public string PlayerName => _playerName;
    public SessionPhase Phase { get; private set; } = SessionPhase.Browsing;
    public Room? Current { get; private set; }
    public string? StatusMessage { get; private set; }

    public bool CanStart =>
        Phase == SessionPhase.Hosting &&
        Current is { } room &&
        room.Players.Count > 1 &&
        room.Players.All(p => p.Ready);

    public void Host(string roomName, string track)
    {
        Current = new Room(Guid.NewGuid(), roomName, track, RoomState.MaxPlayers,
            [new Player(_playerName, "", false)]);
        Phase = SessionPhase.Hosting;
        StatusMessage = null;
        _lastHeard.Clear();
    }

    public bool Join(Room room)
    {
        var distinctNames = room.Players.DistinctBy(p => p.Name).ToList();
        if (distinctNames.Count >= room.MaxPlayers) return false;
        if (distinctNames.Any(p => p.Name == _playerName)) return false;

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
        Current = null;
        Phase = SessionPhase.Browsing;
        StatusMessage = null;
        _lastHeard.Clear();
    }

    public void SetReady(string playerName, bool ready) =>
        UpdatePlayer(playerName, p => p with { Ready = ready });

    public void SetCar(string playerName, string car) =>
        UpdatePlayer(playerName, p => p with { Car = car });

    /// <summary>The host's view of the room, adopted wholesale.</summary>
    public void OnRemoteState(Room room)
    {
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

        if (deduped.Count != room.Players.Count)
            room = room with { Players = deduped };

        Current = room;
        _hostLastHeard = _clock();
        foreach (var player in room.Players)
            if (player.Name != _playerName)
                _lastHeard[player.Name] = _clock();
    }

    public void OnHeard(string playerName) => _lastHeard[playerName] = _clock();

    public void Tick()
    {
        if (Current is not { } room) return;
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
