# Multiplayer Alpha Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Selecting Simulation mode opens a LAN multiplayer lobby instead: players find each other's rooms, join, pick a car, and mark ready, until the host can start.

**Architecture:** The game is never modified. The existing `pre` hook on `gt2_load_overlay` recognises the Simulation overlay's entry point and, instead of loading it, blocks inside the hook while pumping the host window — the game is frozen mid-call and unaware. The screens are ImGui panels registered through the runtime's public `PanelManager`, so the runtime needs no change either. Room state lives in C#, where the netcode will need it.

**Tech Stack:** C# / .NET 10, xunit, ImGuiNET (already used by the runtime's debug panels), UDP sockets from `System.Net.Sockets`.

## Global Constraints

- Target framework `net10.0`, matching every other project here.
- **Change no game code and no generated code.** All new code lives under `patches/multiplayer/`.
- **Change the RecompOne submodule only if unavoidable.** `PanelManager.Register` and `Runtime.PumpHost` are public; the alpha should need nothing more. If a task finds it does, stop and report rather than editing the submodule.
- LAN only: UDP, no server, no NAT traversal, no accounts.
- Host is authoritative and retransmits **whole room state** on every change, never deltas.
- Timeout for a silent host or client is **3 seconds**.
- Maximum **6** players per room.
- Never use `--no-verify`.
- Reference spec: `docs/superpowers/specs/2026-08-22-multiplayer-alpha-design.md`.

## File structure

| file | responsibility |
|---|---|
| `patches/multiplayer/RoomState.cs` | the data: room, player, and their serialisation |
| `patches/multiplayer/Session.cs` | the state machine: join, leave, ready, timeouts |
| `patches/multiplayer/LanDiscovery.cs` | UDP broadcast announce and listen |
| `patches/multiplayer/MultiplayerPanel.cs` | the three screens |
| `patches/multiplayer/ModeHook.cs` | diverts Simulation, pauses the game, runs the lobby |
| `tests/GT2Port.Tests/RoomStateTests.cs` | serialisation round-trips |
| `tests/GT2Port.Tests/SessionTests.cs` | state machine |
| `tests/GT2Port.Tests/LanDiscoveryTests.cs` | loopback discovery |

---

### Task 1: Room state and its wire format

The data every other task passes around, and the only part of the protocol
that touches bytes. Written first so later tasks have concrete types.

**Files:**
- Create: `patches/multiplayer/RoomState.cs`
- Create: `tests/GT2Port.Tests/RoomStateTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `record Player(string Name, string Car, bool Ready)`
  - `record Room(Guid Id, string Name, string Track, int MaxPlayers, IReadOnlyList<Player> Players)`
  - `static byte[] RoomState.Serialise(Room room)`
  - `static bool RoomState.TryDeserialise(ReadOnlySpan<byte> data, out Room room)` — false on truncated or malformed input, never throws

- [ ] **Step 1: Write the failing tests**

Create `tests/GT2Port.Tests/RoomStateTests.cs`:

```csharp
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class RoomStateTests
{
    static Room Sample() => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        "Ian's room", "Trial Mountain", 6,
        [new Player("ian", "Skyline", true), new Player("guest", "Supra", false)]);

    [Fact]
    public void Round_trips_a_room()
    {
        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(Sample()), out var back));

        var original = Sample();
        Assert.Equal(original.Id, back.Id);
        Assert.Equal(original.Name, back.Name);
        Assert.Equal(original.Track, back.Track);
        Assert.Equal(original.MaxPlayers, back.MaxPlayers);
        Assert.Equal(original.Players.Count, back.Players.Count);
    }

    [Fact]
    public void Round_trips_every_player_field()
    {
        RoomState.TryDeserialise(RoomState.Serialise(Sample()), out var back);

        Assert.Equal("ian", back.Players[0].Name);
        Assert.Equal("Skyline", back.Players[0].Car);
        Assert.True(back.Players[0].Ready);
        Assert.False(back.Players[1].Ready);
    }

    [Fact]
    public void Round_trips_a_room_with_no_players()
    {
        var empty = Sample() with { Players = [] };
        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(empty), out var back));
        Assert.Empty(back.Players);
    }

    [Fact]
    public void Rejects_a_truncated_packet_without_throwing()
    {
        var data = RoomState.Serialise(Sample());
        Assert.False(RoomState.TryDeserialise(data.AsSpan(0, data.Length / 2), out _));
    }

    [Fact]
    public void Rejects_an_empty_packet()
    {
        Assert.False(RoomState.TryDeserialise([], out _));
    }

    [Fact]
    public void Rejects_garbage_without_throwing()
    {
        var junk = new byte[64];
        Random.Shared.NextBytes(junk);
        // Either it is rejected, or it happens to parse - it must not throw.
        RoomState.TryDeserialise(junk, out _);
    }

    [Fact]
    public void Fits_a_full_room_in_one_datagram()
    {
        var full = Sample() with
        {
            Players = [.. Enumerable.Range(0, 6)
                .Select(i => new Player($"player{i}", "Some Long Car Name GT-R V-Spec", true))],
        };
        // Whole state is retransmitted on every change; it has to fit one packet.
        Assert.True(RoomState.Serialise(full).Length < 1200);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj --filter RoomStateTests
```

Expected: build failure — `GT2Port.Multiplayer` does not exist.

- [ ] **Step 3: Write the implementation**

Create `patches/multiplayer/RoomState.cs`:

```csharp
using System.Buffers.Binary;
using System.Text;

namespace GT2Port.Multiplayer;

public record Player(string Name, string Car, bool Ready);

public record Room(Guid Id, string Name, string Track, int MaxPlayers, IReadOnlyList<Player> Players);

/// <summary>
/// The wire format for room state.
///
/// The host retransmits the whole room on every change rather than sending
/// deltas, so a lost packet corrects itself on the next one. That only works
/// while the whole room fits in a single datagram, which the tests pin down.
///
/// Deserialisation never throws: this parses data straight off a socket, where
/// anything at all can arrive.
/// </summary>
public static class RoomState
{
    const byte Version = 1;
    const int MaxPlayers = 6;
    const int MaxStringBytes = 64;

    public static byte[] Serialise(Room room)
    {
        var buffer = new List<byte> { Version };
        buffer.AddRange(room.Id.ToByteArray());
        WriteString(buffer, room.Name);
        WriteString(buffer, room.Track);
        buffer.Add((byte)room.MaxPlayers);
        buffer.Add((byte)room.Players.Count);
        foreach (var player in room.Players)
        {
            WriteString(buffer, player.Name);
            WriteString(buffer, player.Car);
            buffer.Add(player.Ready ? (byte)1 : (byte)0);
        }
        return [.. buffer];
    }

    public static bool TryDeserialise(ReadOnlySpan<byte> data, out Room room)
    {
        room = null!;
        int offset = 0;

        if (!TryByte(data, ref offset, out byte version) || version != Version) return false;
        if (data.Length - offset < 16) return false;
        var id = new Guid(data.Slice(offset, 16));
        offset += 16;

        if (!TryString(data, ref offset, out string name)) return false;
        if (!TryString(data, ref offset, out string track)) return false;
        if (!TryByte(data, ref offset, out byte maxPlayers)) return false;
        if (!TryByte(data, ref offset, out byte count) || count > MaxPlayers) return false;

        var players = new List<Player>(count);
        for (int i = 0; i < count; i++)
        {
            if (!TryString(data, ref offset, out string playerName)) return false;
            if (!TryString(data, ref offset, out string car)) return false;
            if (!TryByte(data, ref offset, out byte ready)) return false;
            players.Add(new Player(playerName, car, ready != 0));
        }

        room = new Room(id, name, track, maxPlayers, players);
        return true;
    }

    static void WriteString(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxStringBytes) bytes = bytes[..MaxStringBytes];
        buffer.Add((byte)bytes.Length);
        buffer.AddRange(bytes);
    }

    static bool TryByte(ReadOnlySpan<byte> data, ref int offset, out byte value)
    {
        if (offset >= data.Length) { value = 0; return false; }
        value = data[offset++];
        return true;
    }

    static bool TryString(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = "";
        if (!TryByte(data, ref offset, out byte length)) return false;
        if (length > MaxStringBytes || data.Length - offset < length) return false;
        value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return true;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj --filter RoomStateTests
```

Expected: PASS, 7 tests.

- [ ] **Step 5: Commit**

```bash
git add patches/multiplayer/RoomState.cs tests/GT2Port.Tests/RoomStateTests.cs
git commit -m "Add room state and its wire format"
```

---

### Task 2: The session state machine

Everything that can be logically wrong lives here, and none of it needs a
socket or a screen. Time is injected so timeouts are tested without waiting.

**Files:**
- Create: `patches/multiplayer/Session.cs`
- Create: `tests/GT2Port.Tests/SessionTests.cs`

**Interfaces:**
- Consumes: `Room`, `Player` from Task 1.
- Produces:
  - `enum SessionPhase { Browsing, Hosting, Joined, Disconnected }`
  - `class Session`
    - `Session(string playerName, Func<DateTime> clock)`
    - `string PlayerName { get; }` — the local player, so callers never guess which row is theirs
    - `SessionPhase Phase { get; }`
    - `Room? Current { get; }`
    - `bool CanStart { get; }` — host, two or more players, all ready
    - `void Host(string roomName, string track)`
    - `bool Join(Room room)` — false when the room is full
    - `void Leave()`
    - `void SetReady(string playerName, bool ready)`
    - `void SetCar(string playerName, string car)`
    - `void OnRemoteState(Room room)` — a client adopting the host's state
    - `void OnHeard(string playerName)` — a keep-alive arrived
    - `void Tick()` — drops players silent for 3s; disconnects if the host is silent
    - `string? StatusMessage { get; }` — why the session ended, for the UI

- [ ] **Step 1: Write the failing tests**

Create `tests/GT2Port.Tests/SessionTests.cs`:

```csharp
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class SessionTests
{
    DateTime _now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
    Session NewSession(string name = "ian") => new(name, () => _now);
    void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    static Room RoomWith(params Player[] players) =>
        new(Guid.NewGuid(), "room", "Trial Mountain", 6, players);

    [Fact]
    public void Starts_out_browsing()
    {
        Assert.Equal(SessionPhase.Browsing, NewSession().Phase);
    }

    [Fact]
    public void Hosting_creates_a_room_containing_the_host()
    {
        var session = NewSession();
        session.Host("Ian's room", "Trial Mountain");

        Assert.Equal(SessionPhase.Hosting, session.Phase);
        Assert.Equal("Ian's room", session.Current!.Name);
        Assert.Single(session.Current.Players);
        Assert.Equal("ian", session.Current.Players[0].Name);
    }

    [Fact]
    public void Joining_adopts_the_room()
    {
        var session = NewSession("guest");
        Assert.True(session.Join(RoomWith(new Player("ian", "", false))));

        Assert.Equal(SessionPhase.Joined, session.Phase);
        Assert.Contains(session.Current!.Players, p => p.Name == "guest");
    }

    [Fact]
    public void Joining_a_full_room_is_refused()
    {
        var full = new Room(Guid.NewGuid(), "room", "track", 2,
            [new Player("a", "", false), new Player("b", "", false)]);

        var session = NewSession("guest");
        Assert.False(session.Join(full));
        Assert.Equal(SessionPhase.Browsing, session.Phase);
    }

    [Fact]
    public void Leaving_returns_to_browsing()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.Leave();

        Assert.Equal(SessionPhase.Browsing, session.Phase);
        Assert.Null(session.Current);
    }

    [Fact]
    public void Start_needs_more_than_one_player()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.SetReady("ian", true);

        Assert.False(session.CanStart);
    }

    [Fact]
    public void Start_needs_everyone_ready()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "", true), new Player("guest", "", false)));

        Assert.False(session.CanStart);
    }

    [Fact]
    public void Host_can_start_when_all_are_ready()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "", true), new Player("guest", "", true)));

        Assert.True(session.CanStart);
    }

    [Fact]
    public void A_client_never_gets_to_start()
    {
        var session = NewSession("guest");
        session.Join(RoomWith(new Player("ian", "", true)));
        session.OnRemoteState(RoomWith(
            new Player("ian", "", true), new Player("guest", "", true)));

        Assert.False(session.CanStart);
    }

    [Fact]
    public void Host_drops_a_player_that_goes_silent()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "", false), new Player("guest", "", false)));
        session.OnHeard("guest");

        Advance(3.5);
        session.Tick();

        Assert.DoesNotContain(session.Current!.Players, p => p.Name == "guest");
    }

    [Fact]
    public void Host_keeps_a_player_that_keeps_talking()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "", false), new Player("guest", "", false)));

        for (int i = 0; i < 4; i++)
        {
            session.OnHeard("guest");
            Advance(1.0);
            session.Tick();
        }

        Assert.Contains(session.Current!.Players, p => p.Name == "guest");
    }

    [Fact]
    public void Client_disconnects_when_the_host_goes_silent()
    {
        var session = NewSession("guest");
        session.Join(RoomWith(new Player("ian", "", false)));
        session.OnRemoteState(RoomWith(
            new Player("ian", "", false), new Player("guest", "", false)));

        Advance(3.5);
        session.Tick();

        Assert.Equal(SessionPhase.Disconnected, session.Phase);
        Assert.NotNull(session.StatusMessage);
    }

    [Fact]
    public void Setting_ready_shows_up_in_the_room()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.SetReady("ian", true);

        Assert.True(session.Current!.Players[0].Ready);
    }

    [Fact]
    public void A_joiner_readies_itself_and_not_the_host()
    {
        // Join appends, so the local player is not row zero for a client.
        var session = NewSession("guest");
        session.Join(RoomWith(new Player("ian", "", false)));
        session.SetReady(session.PlayerName, true);

        Assert.False(session.Current!.Players.Single(p => p.Name == "ian").Ready);
        Assert.True(session.Current.Players.Single(p => p.Name == "guest").Ready);
    }

    [Fact]
    public void Setting_a_car_shows_up_in_the_room()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.SetCar("ian", "Skyline GT-R");

        Assert.Equal("Skyline GT-R", session.Current!.Players[0].Car);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj --filter SessionTests
```

Expected: build failure — `Session` does not exist.

- [ ] **Step 3: Write the implementation**

Create `patches/multiplayer/Session.cs`:

```csharp
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
        Current = new Room(Guid.NewGuid(), roomName, track, 6,
            [new Player(_playerName, "", false)]);
        Phase = SessionPhase.Hosting;
        StatusMessage = null;
        _lastHeard.Clear();
    }

    public bool Join(Room room)
    {
        if (room.Players.Count >= room.MaxPlayers) return false;

        Current = room with { Players = [.. room.Players, new Player(_playerName, "", false)] };
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
        Current = room;
        _hostLastHeard = _clock();
        foreach (var player in room.Players)
            if (player.Name != _playerName && !_lastHeard.ContainsKey(player.Name))
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
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj --filter SessionTests
```

Expected: PASS, 15 tests.

- [ ] **Step 5: Commit**

```bash
git add patches/multiplayer/Session.cs tests/GT2Port.Tests/SessionTests.cs
git commit -m "Add the multiplayer session state machine"
```

---

### Task 3: LAN discovery

Announcing a room and finding other people's. The only task that opens a
socket, so it is the only one whose tests need loopback.

**Files:**
- Create: `patches/multiplayer/LanDiscovery.cs`
- Create: `tests/GT2Port.Tests/LanDiscoveryTests.cs`

**Interfaces:**
- Consumes: `Room`, `RoomState` from Task 1.
- Produces:
  - `class LanDiscovery : IDisposable`
    - `LanDiscovery(int port, Func<DateTime> clock)`
    - `void Announce(Room room)` — broadcast one announcement
    - `IReadOnlyList<Room> Rooms { get; }` — rooms heard recently, own ones excluded
    - `void Tick()` — receives pending packets and expires rooms silent for 3s
    - `Guid LocalRoomId { get; set; }` — announcements carrying this id are ignored

- [ ] **Step 1: Write the failing tests**

Create `tests/GT2Port.Tests/LanDiscoveryTests.cs`:

```csharp
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class LanDiscoveryTests
{
    // A port unlikely to collide with anything else on the machine.
    const int Port = 34719;

    DateTime _now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
    void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    static Room Sample(string name = "Ian's room") =>
        new(Guid.NewGuid(), name, "Trial Mountain", 6, [new Player("ian", "", false)]);

    /// <summary>Gives the datagram time to make it across loopback.</summary>
    static void Settle(LanDiscovery listener)
    {
        for (int i = 0; i < 50 && listener.Rooms.Count == 0; i++)
        {
            listener.Tick();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Finds_an_announced_room()
    {
        using var listener = new LanDiscovery(Port, () => _now);
        using var host = new LanDiscovery(Port, () => _now);

        var room = Sample();
        host.Announce(room);
        Settle(listener);

        Assert.Contains(listener.Rooms, r => r.Id == room.Id);
    }

    [Fact]
    public void Carries_the_room_details()
    {
        using var listener = new LanDiscovery(Port, () => _now);
        using var host = new LanDiscovery(Port, () => _now);

        host.Announce(Sample("Trial Mountain Cup"));
        Settle(listener);

        var found = Assert.Single(listener.Rooms);
        Assert.Equal("Trial Mountain Cup", found.Name);
        Assert.Equal("Trial Mountain", found.Track);
        Assert.Single(found.Players);
    }

    [Fact]
    public void Ignores_its_own_announcements()
    {
        using var host = new LanDiscovery(Port, () => _now);
        var room = Sample();
        host.LocalRoomId = room.Id;

        host.Announce(room);
        for (int i = 0; i < 20; i++) { host.Tick(); Thread.Sleep(10); }

        Assert.Empty(host.Rooms);
    }

    [Fact]
    public void Forgets_a_room_that_stops_announcing()
    {
        using var listener = new LanDiscovery(Port, () => _now);
        using var host = new LanDiscovery(Port, () => _now);

        host.Announce(Sample());
        Settle(listener);
        Assert.NotEmpty(listener.Rooms);

        Advance(3.5);
        listener.Tick();

        Assert.Empty(listener.Rooms);
    }

    [Fact]
    public void Keeps_a_room_that_keeps_announcing()
    {
        using var listener = new LanDiscovery(Port, () => _now);
        using var host = new LanDiscovery(Port, () => _now);

        var room = Sample();
        host.Announce(room);
        Settle(listener);

        for (int i = 0; i < 3; i++)
        {
            Advance(1.0);
            host.Announce(room);
            for (int j = 0; j < 20; j++) { listener.Tick(); Thread.Sleep(5); }
        }

        Assert.NotEmpty(listener.Rooms);
    }

    [Fact]
    public void Survives_a_garbage_datagram()
    {
        using var listener = new LanDiscovery(Port, () => _now);
        using var sender = new System.Net.Sockets.UdpClient();
        sender.EnableBroadcast = true;

        var junk = new byte[32];
        Random.Shared.NextBytes(junk);
        sender.Send(junk, junk.Length,
            new System.Net.IPEndPoint(System.Net.IPAddress.Broadcast, Port));

        for (int i = 0; i < 20; i++) { listener.Tick(); Thread.Sleep(5); }

        Assert.Empty(listener.Rooms);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj --filter LanDiscoveryTests
```

Expected: build failure — `LanDiscovery` does not exist.

- [ ] **Step 3: Write the implementation**

Create `patches/multiplayer/LanDiscovery.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace GT2Port.Multiplayer;

/// <summary>
/// Finds rooms on the local network, and announces one.
///
/// UDP broadcast, so there is no server to run and nothing to configure. The
/// same socket carries the netcode later, which is why this is not TCP.
///
/// A room is remembered only while it keeps announcing: a host that quits or
/// unplugs simply stops, and its room ages out. Nothing has to be told.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    readonly UdpClient _socket;
    readonly int _port;
    readonly Func<DateTime> _clock;
    readonly Dictionary<Guid, (Room Room, DateTime Heard)> _seen = [];

    public LanDiscovery(int port, Func<DateTime> clock)
    {
        _port = port;
        _clock = clock;
        _socket = new UdpClient
        {
            EnableBroadcast = true,
            Client = { ReceiveTimeout = 1 },
        };
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    /// <summary>Announcements carrying this id are our own and are ignored.</summary>
    public Guid LocalRoomId { get; set; }

    public IReadOnlyList<Room> Rooms => [.. _seen.Values.Select(v => v.Room)];

    public void Announce(Room room)
    {
        var data = RoomState.Serialise(room);
        try
        {
            _socket.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, _port));
        }
        catch (SocketException)
        {
            // A broadcast that cannot go out is not worth interrupting the
            // lobby for; the next announcement is a second away.
        }
    }

    public void Tick()
    {
        Receive();
        Expire();
    }

    void Receive()
    {
        while (_socket.Available > 0)
        {
            IPEndPoint? from = null;
            byte[] data;
            try
            {
                data = _socket.Receive(ref from);
            }
            catch (SocketException)
            {
                return;
            }

            if (!RoomState.TryDeserialise(data, out var room)) continue;
            if (room.Id == LocalRoomId) continue;
            _seen[room.Id] = (room, _clock());
        }
    }

    void Expire()
    {
        var now = _clock();
        foreach (var id in _seen.Where(e => now - e.Value.Heard > Timeout)
                                .Select(e => e.Key).ToList())
            _seen.Remove(id);
    }

    public void Dispose() => _socket.Dispose();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj --filter LanDiscoveryTests
```

Expected: PASS, 6 tests.

If a test fails because the machine's firewall blocks loopback broadcast,
report that rather than weakening the test — it means discovery will not work
on this machine and the user needs to know before the acceptance run.

- [ ] **Step 5: Commit**

```bash
git add patches/multiplayer/LanDiscovery.cs tests/GT2Port.Tests/LanDiscoveryTests.cs
git commit -m "Add LAN room discovery"
```

---

### Task 4: The screens

Three screens driven by `Session.Phase`. Registered through the runtime's
public `PanelManager`, so the runtime itself is not touched.

**Files:**
- Create: `patches/multiplayer/MultiplayerPanel.cs`

**Interfaces:**
- Consumes: `Session`, `SessionPhase`, `LanDiscovery`, `Room`, `Player`.
- Produces:
  - `class MultiplayerPanel : RecompOne.Runtime.Host.Window.IPanel`
    - `MultiplayerPanel(Session session, LanDiscovery discovery)`
    - `string Name => "Multiplayer"`
    - `bool IsOpen { get; set; }`
    - `void Draw()`
    - `bool StartRequested { get; }` — set when the host presses Start; the mode hook watches this

There is no unit test here. It is immediate-mode ImGui; a widget test would
cost more than it is worth, and the screens are judged by looking at them.
The acceptance run in Task 5 is what exercises this.

- [ ] **Step 1: Write the panel**

Create `patches/multiplayer/MultiplayerPanel.cs`:

```csharp
using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace GT2Port.Multiplayer;

/// <summary>
/// The lobby screens: room list, room creation, and the room itself.
///
/// Which one is showing follows Session.Phase rather than any state of its
/// own, so the screen cannot disagree with the session behind it.
/// </summary>
public sealed class MultiplayerPanel : IPanel
{
    readonly Session _session;
    readonly LanDiscovery _discovery;

    string _roomName = "";
    string _track = "Trial Mountain";
    string _car = "";
    bool _creating;

    public MultiplayerPanel(Session session, LanDiscovery discovery)
    {
        _session = session;
        _discovery = discovery;
    }

    public string Name => "Multiplayer";
    public bool IsOpen { get; set; } = true;
    public bool StartRequested { get; private set; }

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(640, 420), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin("Multiplayer", ref open))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }

        if (_session.StatusMessage is { } message)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), message);

        switch (_session.Phase)
        {
            case SessionPhase.Browsing:
            case SessionPhase.Disconnected:
                if (_creating) DrawCreate(); else DrawRoomList();
                break;
            case SessionPhase.Hosting:
            case SessionPhase.Joined:
                DrawLobby();
                break;
        }

        IsOpen = open;
        ImGui.End();
    }

    void DrawRoomList()
    {
        ImGui.Text("Rooms on this network");
        ImGui.Separator();

        var rooms = _discovery.Rooms;
        if (rooms.Count == 0)
            ImGui.TextDisabled("Looking for rooms...");

        foreach (var room in rooms)
        {
            ImGui.PushID(room.Id.ToString());
            ImGui.Text($"{room.Name}   {room.Track}   {room.Players.Count}/{room.MaxPlayers}");
            ImGui.SameLine();

            bool full = room.Players.Count >= room.MaxPlayers;
            ImGui.BeginDisabled(full);
            if (ImGui.Button(full ? "Full" : "Join")) _session.Join(room);
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        ImGui.Separator();
        if (ImGui.Button("Create a room")) _creating = true;
    }

    void DrawCreate()
    {
        ImGui.Text("New room");
        ImGui.Separator();
        ImGui.InputText("Name", ref _roomName, 32);
        ImGui.InputText("Track", ref _track, 32);

        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_roomName));
        if (ImGui.Button("Create"))
        {
            _session.Host(_roomName, _track);
            _discovery.LocalRoomId = _session.Current!.Id;
            _creating = false;
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel")) _creating = false;
    }

    void DrawLobby()
    {
        var room = _session.Current!;
        ImGui.Text($"{room.Name}   {room.Track}");
        ImGui.Separator();

        foreach (var player in room.Players)
            ImGui.Text($"{(player.Ready ? "[ready]" : "[    ]")}  {player.Name}  {player.Car}");

        ImGui.Separator();
        if (ImGui.InputText("My car", ref _car, 32))
            _session.SetCar(_session.PlayerName, _car);

        if (ImGui.Button("Ready")) _session.SetReady(_session.PlayerName, true);
        ImGui.SameLine();
        if (ImGui.Button("Not ready")) _session.SetReady(_session.PlayerName, false);

        ImGui.Separator();
        ImGui.BeginDisabled(!_session.CanStart);
        if (ImGui.Button("Start race")) StartRequested = true;
        ImGui.EndDisabled();

        if (_session.Phase == SessionPhase.Hosting && !_session.CanStart)
            ImGui.TextDisabled("Waiting for every player to be ready.");

        ImGui.SameLine();
        if (ImGui.Button("Leave")) _session.Leave();
    }
}
```

- [ ] **Step 2: Confirm it compiles**

```bash
dotnet build GT2Port.csproj 2>&1 | grep -E "error CS|êxito"
```

Expected: no errors.

- [ ] **Step 3: Commit**

```bash
git add patches/multiplayer/MultiplayerPanel.cs
git commit -m "Add the multiplayer lobby screens"
```

---

### Task 5: Divert Simulation mode into the lobby

Wires everything to the game. The hook already exists for overlay activation;
this adds one branch to it.

**Files:**
- Create: `patches/multiplayer/ModeHook.cs`
- Modify: `patches/OverlayHook.cs`
- Modify: `Program.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-4.
- Produces: `static bool ModeHook.TryEnterLobby(uint entryPoint)` — true when the entry point is Simulation's and the lobby has run to completion.

- [ ] **Step 1: Find out which overlay Simulation is**

Do not assume. Run the game, choose Simulation from the menu, and read which
overlay the existing hook reports loading:

```bash
dotnet run --project GT2Port.csproj --no-build 2>&1 | grep "\[Overlay\] load"
```

The spec expects `gt2_05` on the strength of its symbols (`buy_car`,
`garage`, `get_car_price`), but the entry point that actually appears when
Simulation is chosen is what the hook must match. Record it, and take the
matching address from `patches/OverlayEntryPoints.cs`.

If the observed overlay is not `gt2_05`, use what was observed and note the
discrepancy in the commit message.

- [ ] **Step 2: Write the mode hook**

Create `patches/multiplayer/ModeHook.cs`, replacing `SimulationEntryPoint`
with the address found in Step 1:

```csharp
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

        while (!_panel!.StartRequested)
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
```

- [ ] **Step 3: Give the overlay hook its new branch**

In `patches/OverlayHook.cs`, inside `ActivateFromEntry`, before the existing
lookup:

```csharp
        // Simulation mode is where multiplayer lives now. The lobby runs to
        // completion here; the overlay is only loaded if it declines.
        if (Multiplayer.ModeHook.TryEnterLobby(entry)) return;
```

- [ ] **Step 4: Confirm it builds**

```bash
dotnet build GT2Port.csproj 2>&1 | grep -E "error CS|êxito"
```

Expected: no errors.

- [ ] **Step 5: Run the whole suite**

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

Expected: PASS. The count is the 37 that already existed plus 28 added here.

- [ ] **Step 6: Check the game still boots and the lobby appears**

```bash
dotnet run --project GT2Port.csproj --no-build
```

Choose Simulation from the menu. The Multiplayer panel should appear and the
game should stop advancing behind it. Close the window to end the run.

If the game keeps running behind the lobby, the entry point in Step 2 is
wrong — recheck against Step 1's output rather than adjusting the loop.

- [ ] **Step 7: Commit**

```bash
git add patches/multiplayer/ModeHook.cs patches/OverlayHook.cs Program.cs
git commit -m "Divert Simulation mode into the multiplayer lobby"
```

---

### Task 6: Acceptance on a real LAN

The alpha is judged here, not by the unit tests. This task has no code; it is
the run that decides whether the thing works.

**Files:** none.

- [ ] **Step 1: Build a copy for the second machine**

```bash
dotnet publish GT2Port.csproj -c Release -o publish
```

The disc, `settings.json`, `patches/runtime/` and `ovl_patched/` must be
present alongside it, exactly as in the working tree.

- [ ] **Step 2: Run the acceptance**

On two machines on the same network, both running the port:

1. Both: choose Simulation from the menu — the lobby appears on both
2. Machine A: create a room
3. Machine B: the room appears in the list within about two seconds
4. Machine B: join it
5. Both: both players are listed in the lobby
6. Both: press Ready
7. Machine A: "Start race" becomes enabled

- [ ] **Step 3: Check the failure paths too**

1. With both in the lobby, close machine B — A drops it from the list within
   about three seconds
2. Rejoin, then close machine A — B returns to the room list with "The host
   left the room."

- [ ] **Step 4: Record the outcome**

Write what actually happened into
`docs/superpowers/specs/2026-08-22-multiplayer-alpha-design.md` under a new
"Acceptance" heading — including anything that did not work. Commit it.

```bash
git add docs/superpowers/specs/2026-08-22-multiplayer-alpha-design.md
git commit -m "Record the multiplayer alpha acceptance run"
```

---

## Notes for the implementer

**`Program.cs` needs no wiring.** The mode hook builds its session, discovery
and panel the first time it is entered, so nothing has to be set up at start-up.
Task 5 lists `Program.cs` as modified only in case the observed entry point
needs a constant somewhere it can be configured; if it does not, leave it alone.

**Do not edit the RecompOne submodule.** `PanelManager.Register` and
`Runtime.PumpHost` are both public and are all this needs. If something appears
to require a submodule change, stop and report it — it means an assumption in
the spec is wrong.

**The panel is judged by eye.** There are no widget tests, deliberately. If a
screen looks wrong, that is a finding for the user, not a test to write.
