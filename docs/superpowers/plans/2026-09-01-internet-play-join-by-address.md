# Internet Play, Stage 1: Joining by Address — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a player join a room by typing the host's address, so a race can be run between machines that are not on the same network.

**Architecture:** Only one of the two sockets is LAN-bound. `LanDiscovery` uses `IPAddress.Broadcast`, which no router forwards; `LanSession` is already point-to-point UDP with the host relaying to everyone, which is exactly what the internet wants. So this stage leaves the session protocol alone and adds one thing it lacks: a way for a client to reach a host whose room it has never heard announced. A client that knows only an address sends its ordinary intent with an empty room id — a *knock* — and the host answers with room state, which is what a client already adopts. Two protocol additions carry it: the empty room id, and a room secret, because a port open to the internet is scanned within hours.

**Tech Stack:** C# / .NET 10, xUnit, UDP via `System.Net.Sockets.UdpClient`, ImGui.NET for the lobby screens.

## Global Constraints

- The room secret is **never** serialised into room state. It stays on the host and travels only from client to host, in the intent.
- `RoomState`'s wire version does not change in this plan. `LanSession`'s intent version does: `Version` goes from 2 to 3.
- A room with an empty secret accepts anybody, which is what every room does today. Nothing in this plan changes how a LAN room behaves.
- Every value that arrives off a socket is clamped or rejected, never trusted — the rule the room's laps, minutes and stage already follow.
- Tests run with `dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj -c Debug --nologo`. Every task ends with the whole suite green.
- New `LanSession`/`LanDiscovery` tests must bind ports nothing else uses. `LanSessionTests` owns `BasePort + 20..27`; this plan uses `BasePort + 28..31`.

## Why this stage and not the other two

Internet play is three subsystems. Only one of them can be specified honestly today:

| stage | what it is | why not now |
| --- | --- | --- |
| **1. Join by address** | this plan | ready |
| 2. Surviving latency | sequence numbers, timestamps, an interpolation buffer | every parameter of it — how far behind to render, how long a buffer, when to extrapolate — is chosen from measurements that do not exist until stage 1 has been run over a real connection |
| 3. Rendezvous and relay | a small service for room listing, hole punching, relay fallback | the choice between hole punching and relay depends on how many real players fail to connect in stage 1 |

Writing tasks for stages 2 and 3 now would mean inventing numbers and calling them requirements. Each gets its own plan when stage 1 has produced the measurements it needs.

## File Structure

| file | responsibility |
| --- | --- |
| `patches/multiplayer/RoomState.cs` | `Room` gains `Secret`, which is deliberately *not* serialised |
| `patches/multiplayer/LanSession.cs` | intent carries a secret; a knock is an intent with an empty room id; the host answers one |
| `patches/multiplayer/Session.cs` | the `Knocking` phase, and adopting the room that answers |
| `patches/multiplayer/ModeHook.cs` | a socket while knocking, and the typed address as the host address |
| `patches/multiplayer/MultiplayerPanel.cs` | the address and secret fields on the room list, and the secret on the create screen |
| `tests/GT2Port.Tests/JoinByAddressTests.cs` | new: the knock, the secret, and the phase |

---

### Task 1: A room can have a secret, and the host checks it

**Files:**
- Modify: `patches/multiplayer/RoomState.cs`
- Modify: `patches/multiplayer/LanSession.cs`
- Modify: `patches/multiplayer/Session.cs`
- Test: `tests/GT2Port.Tests/JoinByAddressTests.cs` (create)

**Interfaces:**
- Produces: `Room.Secret` (string, default `""`); `LanSession.ClientIntent.Secret` (string, default `""`); `Session.Host(..., string secret = "")`.
- Consumes: nothing from later tasks.

- [ ] **Step 1: Write the failing test**

Create `tests/GT2Port.Tests/JoinByAddressTests.cs`:

```csharp
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// Reaching a host that never announced itself, which is every host that is not
/// on this network. A port open to the internet is scanned within hours, so the
/// room carries a secret and the host checks it.
/// </summary>
public class JoinByAddressTests
{
    [Fact]
    public void A_rooms_secret_never_goes_out_in_its_room_state()
    {
        var session = new Session("ian", () => DateTime.UtcNow);
        session.Host("ian's room", "seattle_short", "special", 6, secret: "hunter2");

        Assert.Equal("hunter2", session.Current!.Secret);

        Assert.True(RoomState.TryDeserialise(
            RoomState.Serialise(session.Current!), out var published));

        Assert.Equal("", published.Secret);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj -c Debug --nologo --filter JoinByAddressTests`

Expected: FAIL to compile — `Host` has no `secret` parameter and `Room` has no `Secret`.

- [ ] **Step 3: Give a room a secret**

In `patches/multiplayer/RoomState.cs`, change the `Room` record's parameter list (leave the body as it is):

```csharp
public record Room(Guid Id, string Name, string Track, string CarGroup, int MaxPlayers,
                   IReadOnlyList<Player> Players, byte Laps = RaceLaps.AsBuilt,
                   ushort Minutes = TimedRace.ByLaps,
                   RoomStage Stage = RoomStage.Racing,
                   string Secret = "")
```

Add this to the record's XML summary, above `public record Room`:

```
/// <paramref name="Secret"/> is what a client has to say to be let in, and it is
/// the one thing about a room that is never published. A room announced on a
/// local network is announced to everyone on it, so a secret in that
/// announcement would be a secret told to the people it is meant to keep out.
/// It travels one way only: from a client that is asking, to the host that
/// decides. Empty means the room is open, which is what every room was before
/// there was anything to keep out.
```

`RoomState.Serialise` and `TryDeserialise` are **not** changed. `TryDeserialise` builds its `Room` without a secret, so a deserialised room's secret is `""` by construction.

- [ ] **Step 4: Let a host set one**

In `patches/multiplayer/Session.cs`, extend `Host`'s signature and the room it builds:

```csharp
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
```

- [ ] **Step 5: Run the test and watch it pass**

Run: `dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj -c Debug --nologo --filter JoinByAddressTests`

Expected: PASS, 1 test.

- [ ] **Step 6: Write the failing test for the check itself**

Append to `tests/GT2Port.Tests/JoinByAddressTests.cs`, inside the class:

```csharp
    /// <summary>
    /// The secret is checked before anything else is believed. A room with no
    /// secret lets anybody in, which is what a room on a local network has
    /// always done.
    /// </summary>
    [Theory]
    [InlineData("", "", true)]
    [InlineData("", "anything", true)]
    [InlineData("hunter2", "hunter2", true)]
    [InlineData("hunter2", "", false)]
    [InlineData("hunter2", "wrong", false)]
    public void A_client_is_let_in_only_when_it_says_the_rooms_secret(
        string roomSecret, string said, bool letIn)
    {
        Assert.Equal(letIn, LanSession.SaidTheSecret(roomSecret, said));
    }
```

- [ ] **Step 7: Run it and watch it fail**

Run: `dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj -c Debug --nologo --filter JoinByAddressTests`

Expected: FAIL to compile — `LanSession.SaidTheSecret` does not exist.

- [ ] **Step 8: Carry a secret in the intent and check it**

In `patches/multiplayer/LanSession.cs`, bump the intent version:

```csharp
    const byte Version = 3;
```

Extend the record and both halves of its wire format:

```csharp
    internal readonly record struct ClientIntent(
        Guid RoomId, string Name, string Car, bool Ready, bool Leaving,
        byte Colour = 0, bool Watching = false, string Secret = "");
```

In `Serialise`, after the colour:

```csharp
        buffer.Add(intent.Colour);
        WriteString(buffer, intent.Secret);
        return [.. buffer];
```

In `TryDeserialise`, after the colour and before the `intent = new ClientIntent(...)`:

```csharp
        if (!TryByte(span, ref offset, out byte colour)) return false;
        if (!TryString(span, ref offset, out string secret)) return false;

        intent = new ClientIntent(roomId, name, car,
            Ready: (flags & ReadyFlag) != 0, Leaving: (flags & LeavingFlag) != 0,
            Colour: colour, Watching: (flags & WatchingFlag) != 0, Secret: secret);
        return true;
```

Add the check as a static, next to the wire format:

```csharp
    /// <summary>
    /// Whether a client that said <paramref name="said"/> may join a room whose
    /// secret is <paramref name="roomSecret"/>.
    ///
    /// A room with no secret is open, which is what every room was before there
    /// was an address to reach one at. A room with one is closed to everything
    /// that does not repeat it exactly - and on a port that faces the internet,
    /// most of what arrives is a scanner rather than a player.
    /// </summary>
    internal static bool SaidTheSecret(string roomSecret, string said) =>
        roomSecret.Length == 0 || string.Equals(roomSecret, said, StringComparison.Ordinal);
```

In `HostTick`, reject a wrong secret before the room id is even looked at:

```csharp
            if (KeptAResult(data, from)) continue;

            if (!TryDeserialise(data, out var intent)) continue;
            if (!SaidTheSecret(room.Secret, intent.Secret)) continue;
            if (intent.RoomId != room.Id) continue;
```

In both places that build an intent to send — `HostTick`'s client counterpart and the leave message, at the two `new ClientIntent(current.Id, session.PlayerName, ...)` call sites in `ClientTick` — pass the secret the client was given:

```csharp
        var intent = new ClientIntent(current.Id, session.PlayerName,
            mine?.Car ?? "", mine?.Ready ?? false, Leaving: false,
            mine?.Colour ?? 0, mine?.Watching ?? false, Secret);
```

and add the field it reads, beside `_hosting`:

```csharp
    /// <summary>
    /// What this client says to be let in. Empty on a host, which never has to
    /// ask itself anything.
    /// </summary>
    internal string Secret { get; set; } = "";
```

- [ ] **Step 9: Run the tests and watch them pass**

Run: `dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj -c Debug --nologo`

Expected: PASS, all tests. `LanSessionTests` builds its intents through `LanSession`, so the version bump does not strand them.

- [ ] **Step 10: Commit**

```bash
git add patches/multiplayer/RoomState.cs patches/multiplayer/LanSession.cs patches/multiplayer/Session.cs tests/GT2Port.Tests/JoinByAddressTests.cs
git commit -m "Let a room keep a secret, and never publish it"
```

---

### Task 2: A client that knows only an address can knock

**Files:**
- Modify: `patches/multiplayer/LanSession.cs`
- Modify: `patches/multiplayer/Session.cs`
- Test: `tests/GT2Port.Tests/JoinByAddressTests.cs`

**Interfaces:**
- Consumes: `LanSession.SaidTheSecret`, `LanSession.ClientIntent.Secret`, `Room.Secret` from Task 1.
- Produces: `SessionPhase.Knocking`; `Session.Knock(string address, string secret)`; `Session.KnockingAt` (string); `Session.KnockingSecret` (string); `LanSession.SendKnock(IPEndPoint host, string name, string secret)`.

- [ ] **Step 1: Write the failing test**

Append to `tests/GT2Port.Tests/JoinByAddressTests.cs`, inside the class:

```csharp
    /// <summary>
    /// A client that has only an address has never heard the room's id, so it
    /// cannot put one in its intent. It sends the empty one instead, which is
    /// what a knock is: the host answers a knock with room state, and room
    /// state is the thing a client already knows how to adopt.
    /// </summary>
    [Fact]
    public void A_knock_carries_no_room_id()
    {
        var session = new Session("les", () => DateTime.UtcNow);

        Assert.True(session.Knock("192.168.0.9:34719", "hunter2"));

        Assert.Equal(SessionPhase.Knocking, session.Phase);
        Assert.Equal("192.168.0.9:34719", session.KnockingAt);
        Assert.Equal("hunter2", session.KnockingSecret);
        Assert.Null(session.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an address")]
    [InlineData("1.2.3.4:70000")]
    public void An_address_that_is_not_one_is_refused_before_anything_is_sent(string typed)
    {
        var session = new Session("les", () => DateTime.UtcNow);

        Assert.False(session.Knock(typed, ""));

        Assert.Equal(SessionPhase.Browsing, session.Phase);
        Assert.NotNull(session.StatusMessage);
    }

    /// <summary>
    /// And the host answers one from a sender it has never heard of, which is
    /// the whole point: on the internet nobody announced anything.
    /// </summary>
    [Fact]
    public void A_host_takes_a_knock_from_a_stranger_and_answers_it()
    {
        const int hostPort = LanSessionTests.BasePort + 28;

        var host = new Session("ian", () => DateTime.UtcNow);
        host.Host("ian's room", "seattle_short", "special", 6, secret: "hunter2");

        using var wire = LanSession.ForHost(hostPort, () => DateTime.UtcNow);
        using var stranger = new System.Net.Sockets.UdpClient(0);

        var knock = LanSession.Serialise(new LanSession.ClientIntent(
            Guid.Empty, "les", "", Ready: false, Leaving: false, Secret: "hunter2"));

        stranger.Send(knock, knock.Length,
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, wire.BoundPort));

        LanSessionTests.WaitForDelivery(wire);
        wire.HostTick(host);

        Assert.Contains(host.Current!.Players, p => p.Name == "les");
    }

    [Fact]
    public void And_ignores_one_that_does_not_know_the_secret()
    {
        const int hostPort = LanSessionTests.BasePort + 29;

        var host = new Session("ian", () => DateTime.UtcNow);
        host.Host("ian's room", "seattle_short", "special", 6, secret: "hunter2");

        using var wire = LanSession.ForHost(hostPort, () => DateTime.UtcNow);
        using var stranger = new System.Net.Sockets.UdpClient(0);

        var knock = LanSession.Serialise(new LanSession.ClientIntent(
            Guid.Empty, "les", "", Ready: false, Leaving: false, Secret: "wrong"));

        stranger.Send(knock, knock.Length,
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, wire.BoundPort));

        LanSessionTests.WaitForDelivery(wire);
        wire.HostTick(host);

        Assert.DoesNotContain(host.Current!.Players, p => p.Name == "les");
    }
```

- [ ] **Step 2: Make the helpers the test borrows visible**

In `tests/GT2Port.Tests/LanSessionTests.cs`, change the class and the two members the new tests use from private to internal, leaving their bodies alone:

```csharp
public class LanSessionTests
{
    internal const int BasePort = 34820;
```

and

```csharp
    internal static void WaitForDelivery(LanSession session)
```

If `BasePort` is currently declared with a different value, keep that value and only add `internal`.

- [ ] **Step 3: Run it and watch it fail**

Run: `dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj -c Debug --nologo --filter JoinByAddressTests`

Expected: FAIL to compile — `SessionPhase.Knocking`, `Session.Knock`, `KnockingAt` and `KnockingSecret` do not exist.

- [ ] **Step 4: Add the phase and the knock**

In `patches/multiplayer/Session.cs`, extend the phase enum:

```csharp
public enum SessionPhase { Browsing, Hosting, Joined, Disconnected, Knocking }
```

Add to `Session`, beside `Join`:

```csharp
    /// <summary>Where this client is knocking, and what it is saying.</summary>
    public string KnockingAt { get; private set; } = "";
    public string KnockingSecret { get; private set; } = "";

    /// <summary>
    /// Starts asking a host at a typed address to be let in.
    ///
    /// A room found by announcement arrives whole - its id, its track, its
    /// players - and Join is handed all of it. A room found by address arrives
    /// as nothing at all, so there is a phase between browsing and being in it:
    /// knocking, which is this client repeating an intent at an address and
    /// waiting to be answered with room state.
    ///
    /// Returns false when what was typed is not an address, which is worth
    /// saying before a single datagram is sent to nowhere.
    /// </summary>
    public bool Knock(string address, string secret)
    {
        if (!TryReadAddress(address, out _))
        {
            StatusMessage = $"\"{address.Trim()}\" is not an address and a port.";
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
```

- [ ] **Step 5: Let the host answer a knock**

In `patches/multiplayer/LanSession.cs`, change the room-id check in `HostTick` so an empty id means "I have never heard of you":

```csharp
            if (!TryDeserialise(data, out var intent)) continue;
            if (!SaidTheSecret(room.Secret, intent.Secret)) continue;

            // Guid.Empty is a knock: a client that reached this host by address
            // has never heard the room announced and cannot name it. Anything
            // else naming the wrong room is crossed wires or forgery.
            if (intent.RoomId != Guid.Empty && intent.RoomId != room.Id) continue;
```

Add the send side, beside `SendPlace`:

```csharp
    /// <summary>
    /// Asks a host at a known address to be let in, without knowing anything
    /// about its room - not even its id.
    ///
    /// It is an ordinary intent with an empty room id, so the host needs no new
    /// message to understand one and answers it the way it answers every
    /// intent: with room state.
    /// </summary>
    public void SendKnock(IPEndPoint host, string name, string secret)
    {
        if (_disposed) return;

        Secret = secret;
        var knock = Serialise(new ClientIntent(
            Guid.Empty, name, "", Ready: false, Leaving: false, Secret: secret));

        Send(knock, host);
    }
```

- [ ] **Step 6: Run the tests and watch them pass**

Run: `dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj -c Debug --nologo`

Expected: PASS, all tests.

- [ ] **Step 7: Commit**

```bash
git add patches/multiplayer/LanSession.cs patches/multiplayer/Session.cs tests/GT2Port.Tests/JoinByAddressTests.cs tests/GT2Port.Tests/LanSessionTests.cs
git commit -m "Let a client knock at an address it was told, knowing nothing else"
```

---

### Task 3: A knocking client holds a socket and adopts the room that answers

**Files:**
- Modify: `patches/multiplayer/Session.cs`
- Modify: `patches/multiplayer/ModeHook.cs`
- Test: `tests/GT2Port.Tests/JoinByAddressTests.cs`

**Interfaces:**
- Consumes: `SessionPhase.Knocking`, `Session.KnockingAt`, `Session.KnockingSecret`, `LanSession.SendKnock` from Task 2.
- Produces: nothing later tasks depend on beyond the behaviour.

- [ ] **Step 1: Write the failing test**

Append to `tests/GT2Port.Tests/JoinByAddressTests.cs`, inside the class:

```csharp
    /// <summary>
    /// The answer to a knock is room state, and adopting it is what joining by
    /// address means. The room's id is learned here and not before, so the
    /// guard that rejects state for a different room cannot apply yet.
    /// </summary>
    [Fact]
    public void The_room_that_answers_a_knock_is_the_room_this_client_joins()
    {
        var session = new Session("les", () => DateTime.UtcNow);
        session.Knock("192.168.0.9:34719", "");

        session.OnRemoteState(new Room(
            Guid.NewGuid(), "ian's room", "seattle_short", "special", 6,
            [new Player("ian", "buc9n", true), new Player("les", "", false)]));

        Assert.Equal(SessionPhase.Joined, session.Phase);
        Assert.Equal("ian's room", session.Current!.Name);
    }

    /// <summary>
    /// A host that answers without a row for this client answered a full room.
    /// Adopting it would leave a client sitting in a room it is not in.
    /// </summary>
    [Fact]
    public void A_room_with_no_row_for_this_client_is_not_joined()
    {
        var session = new Session("les", () => DateTime.UtcNow);
        session.Knock("192.168.0.9:34719", "");

        session.OnRemoteState(new Room(
            Guid.NewGuid(), "ian's room", "seattle_short", "special", 6,
            [new Player("ian", "buc9n", true)]));

        Assert.Equal(SessionPhase.Disconnected, session.Phase);
        Assert.Null(session.Current);
    }

    /// <summary>
    /// A knocking client needs the socket a joined one needs - it is already
    /// talking to the host - and browsing still needs none.
    /// </summary>
    [Fact]
    public void A_knocking_client_is_given_a_client_socket()
    {
        Assert.Equal(
            ModeHook.SocketAction.RebuildAsClient,
            ModeHook.DecideSocketAction(null, SessionPhase.Knocking));

        Assert.Equal(
            ModeHook.SocketAction.Keep,
            ModeHook.DecideSocketAction(SessionPhase.Joined, SessionPhase.Knocking));

        Assert.Equal(
            ModeHook.SocketAction.Drop,
            ModeHook.DecideSocketAction(SessionPhase.Joined, SessionPhase.Browsing));
    }
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj -c Debug --nologo --filter JoinByAddressTests`

Expected: FAIL — the first two on `Phase` being `Knocking` rather than `Joined` or `Disconnected`, the third on `DecideSocketAction` returning `Drop` for `Knocking`.

- [ ] **Step 3: Adopt the room that answers**

In `patches/multiplayer/Session.cs`, at the top of `OnRemoteState`, directly after the `Hosting` guard:

```csharp
        if (Phase == SessionPhase.Hosting) return;

        // A knock is answered with room state, and this is the first thing this
        // client has ever been told about the room - including its id, so the
        // guard below that rejects state for a different room has nothing to
        // compare yet. A room that answers without a row for this client is a
        // full one, reported the same way the joined path reports it.
        if (Phase == SessionPhase.Knocking)
        {
            if (!room.Players.Any(p => p.Name == _playerName))
            {
                Current = null;
                Phase = SessionPhase.Disconnected;
                StatusMessage = "The room is full.";
                return;
            }

            Phase = SessionPhase.Joined;
            StatusMessage = null;
        }
```

- [ ] **Step 4: Give a knocking client a socket**

In `patches/multiplayer/ModeHook.cs`, replace `DecideSocketAction` entirely:

```csharp
    internal static SocketAction DecideSocketAction(SessionPhase? currentRole, SessionPhase phase)
    {
        if (phase == SessionPhase.Hosting)
            return currentRole == SessionPhase.Hosting ? SocketAction.Keep : SocketAction.RebuildAsHost;

        // Knocking holds the same socket as Joined and for the same reason:
        // both are a client talking to one host. A knocking client that had to
        // wait for a socket would have nothing to knock with.
        if (phase is SessionPhase.Joined or SessionPhase.Knocking)
            return currentRole == SessionPhase.Joined ? SocketAction.Keep : SocketAction.RebuildAsClient;

        // Browsing and Disconnected hold no socket: drop one if there still
        // is one, otherwise there is nothing to do.
        return currentRole is null ? SocketAction.Keep : SocketAction.Drop;
    }
```

The role recorded when that socket is built stays `SessionPhase.Joined`, which `RunLobby` already does — so moving from knocking to joined comes back `Keep` and the socket is not rebuilt underneath a client mid-handshake.

- [ ] **Step 5: Knock from the lobby loop**

In `patches/multiplayer/ModeHook.cs`, in `RunLobby`'s loop, beside the `Hosting` and `Joined` branches that tick the session:

```csharp
                if (_session.Phase == SessionPhase.Hosting)
                {
                    _lanSession?.HostTick(_session);
                }
                else if (_session.Phase == SessionPhase.Knocking &&
                         Session.TryReadAddress(_session.KnockingAt, out var knockingAt))
                {
                    // Repeated, not sent once: this is a datagram to a machine
                    // that has never heard of us, over a network that loses
                    // them, and there is nothing to retry it if it goes
                    // missing. The host's answer is what ends this.
                    _raceHost = knockingAt.Address;
                    _lanSession?.SendKnock(knockingAt, _session.PlayerName, _session.KnockingSecret);
                    _lanSession?.ClientTick(_session, knockingAt.Address);
                }
```

The `else if` for `SessionPhase.Joined` that follows it is left alone here and replaced in the next step.

- [ ] **Step 6: Keep the typed address once the room is joined**

Still in `RunLobby`, the `Joined` branch learns the host's address from discovery, which a room joined by address never announced - so as it stands, a client that knocked its way in would fall through it every tick and never speak to the host again. Replace the whole branch, including its condition:

```csharp
                else if (_session.Phase == SessionPhase.Joined)
                {
                    // Discovery knows the address of a room it heard announced.
                    // A room joined by address was never announced, and the
                    // address it was reached at is the one already kept.
                    if (_discovery.TryGetHostAddress(_session.Current!.Id, out var hostAddress))
                        _raceHost = hostAddress;

                    if (_raceHost is { } host) _lanSession?.ClientTick(_session, host);
                }
```

- [ ] **Step 7: Run the tests and watch them pass**

Run: `dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj -c Debug --nologo`

Expected: PASS, all tests.

- [ ] **Step 8: Commit**

```bash
git add patches/multiplayer/Session.cs patches/multiplayer/ModeHook.cs tests/GT2Port.Tests/JoinByAddressTests.cs
git commit -m "Adopt the room that answers a knock, and keep the address it came from"
```

---

### Task 4: The room list can be given an address

**Files:**
- Modify: `patches/multiplayer/MultiplayerPanel.cs`
- Test: none — this task is ImGui drawing, which the panel has no test harness for; its behaviour is the three tasks below it, all covered.

**Interfaces:**
- Consumes: `Session.Knock`, `Session.KnockingAt`, `SessionPhase.Knocking` from Tasks 2 and 3; `Session.Host(..., secret)` from Task 1.
- Produces: nothing.

- [ ] **Step 1: Add the fields the screens need**

In `patches/multiplayer/MultiplayerPanel.cs`, beside `_roomName`:

```csharp
    /// <summary>What the room list's "join by address" row holds.</summary>
    string _address = "";
    string _addressSecret = "";

    /// <summary>And what the create screen's secret field holds.</summary>
    string _secret = "";
```

- [ ] **Step 2: Offer the address on the room list**

In `DrawRoomList`, directly after the `Rooms on this network` block's `foreach` loop closes and before the `Create a room` button:

```csharp
        ImGui.Separator();
        ImGui.TextUnformatted("Or join by address");

        ImGui.InputText("Address", ref _address, 64);
        ImGui.InputText("Secret", ref _addressSecret, 64);

        ImGui.BeginDisabled(_address.Trim().Length == 0);
        if (ImGui.Button("Join by address")) _session.Knock(_address, _addressSecret);
        ImGui.EndDisabled();

        if (_session.Phase == SessionPhase.Knocking)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop")) _session.Leave();
            ImGui.TextDisabled($"Knocking at {_session.KnockingAt}...");
        }
```

- [ ] **Step 3: Offer the secret when a room is made**

In `DrawCreate`, directly after the `Qualifying first` checkbox:

```csharp
        ImGui.InputText("Secret", ref _secret, 64);
        ImGui.TextDisabled("Leave empty to let anybody in. A room reachable from the internet should have one.");
```

and pass it when the room is made:

```csharp
            _session.Host(_roomName, _track, _carGroup, _maxPlayers,
                          _laps, _byTheClock ? _minutes : TimedRace.ByLaps, _qualifying, _secret);
```

- [ ] **Step 4: Show the address a host should hand out**

In `DrawLobby`, after the line that draws the room's name and track:

```csharp
        if (_session.Phase == SessionPhase.Hosting)
            ImGui.TextDisabled($"Others join at your address, port {ModeHook.SessionPortNumber}");
```

and in `patches/multiplayer/ModeHook.cs`, expose the number the panel needs, beside `const int SessionPort = 34719;`:

```csharp
    /// <summary>The port a client reaches this host on, for the lobby to say.</summary>
    public static int SessionPortNumber => SessionPort;
```

- [ ] **Step 5: Build and run the whole suite**

Run: `dotnet build GT2Port.csproj -c Debug -v q --nologo`

Expected: `0 Erro(s)`.

Run: `dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj -c Debug --nologo`

Expected: PASS, all tests.

- [ ] **Step 6: Commit**

```bash
git add patches/multiplayer/MultiplayerPanel.cs patches/multiplayer/ModeHook.cs
git commit -m "Let a player type the address of a room nobody announced"
```

---

## What running it has to measure

This stage is finished when two machines on different networks have raced, and it is worth nothing until somebody has read the numbers it produces. Stage 2 is specified from these and cannot be written before them:

1. **Round trip.** The gap between a place being sent and the same seat's next place arriving, at the host and at a client. The interpolation delay in stage 2 is chosen from its spread, not its average.
2. **Loss.** How many places never arrive, as a share. Below about 1% an interpolation buffer alone is enough; above it, extrapolation earns its keep.
3. **Reordering.** How often a place arrives after a newer one. If this is never zero, the sequence number in stage 2 is not optional.
4. **Whether the start barrier still holds.** It waits 20 seconds and then starts anyway; over a real connection, the wait it actually takes is the number that says whether that patience is right.
5. **How it looks.** Whether remote cars are merely late or actually wrong, which decides how much of stage 2 is interpolation and how much is dead reckoning.

The port already prints most of this. What it does not print is the round trip, the loss and the reordering, because on a LAN they were all zero — adding those counters is the first task of stage 2's plan.
