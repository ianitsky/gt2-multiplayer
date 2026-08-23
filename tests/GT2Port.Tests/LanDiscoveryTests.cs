using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class LanDiscoveryTests
{
    // Every test gets its own port off this base so a stray in-flight
    // broadcast from one test's host can never land in another test's
    // listener (Finding 4). Offsets below are unique per test and obvious.
    const int BasePort = 34719;

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

    /// <summary>
    /// Forces the next send on a LanDiscovery's underlying socket to fail with
    /// a real SocketException, without disposing the socket or mocking anything:
    /// shutting down the send direction of a live UDP socket makes the OS itself
    /// refuse the next send.
    /// </summary>
    static void BreakSending(LanDiscovery discovery)
    {
        var field = typeof(LanDiscovery).GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("LanDiscovery no longer has a _socket field.");
        var socket = (UdpClient)field.GetValue(discovery)!;
        socket.Client.Shutdown(SocketShutdown.Send);
    }

    [Fact]
    public void Finds_an_announced_room()
    {
        const int port = BasePort + 0;
        using var listener = new LanDiscovery(port, () => _now);
        using var host = new LanDiscovery(port, () => _now);

        var room = Sample();
        host.Announce(room);
        Settle(listener);

        Assert.Contains(listener.Rooms, r => r.Id == room.Id);
    }

    [Fact]
    public void Carries_the_room_details()
    {
        const int port = BasePort + 1;
        using var listener = new LanDiscovery(port, () => _now);
        using var host = new LanDiscovery(port, () => _now);

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
        const int port = BasePort + 2;
        using var host = new LanDiscovery(port, () => _now);
        using var other = new LanDiscovery(port, () => _now);

        // Positive precondition: prove the transport actually works on this
        // machine before trusting a negative assertion about it (Finding 1).
        // A room announced from a *different* id must be received.
        var foreign = Sample("Someone else's room");
        other.Announce(foreign);
        Settle(host);
        Assert.Contains(host.Rooms, r => r.Id == foreign.Id);

        var own = Sample();
        host.LocalRoomId = own.Id;
        host.Announce(own);
        for (int i = 0; i < 20; i++) { host.Tick(); Thread.Sleep(10); }

        Assert.DoesNotContain(host.Rooms, r => r.Id == own.Id);
        Assert.Single(host.Rooms); // only the foreign room, self stayed filtered out
    }

    [Fact]
    public void Forgets_a_room_that_stops_announcing()
    {
        const int port = BasePort + 3;
        using var listener = new LanDiscovery(port, () => _now);
        using var host = new LanDiscovery(port, () => _now);

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
        const int port = BasePort + 4;
        using var listener = new LanDiscovery(port, () => _now);
        using var host = new LanDiscovery(port, () => _now);

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
        const int port = BasePort + 5;
        using var listener = new LanDiscovery(port, () => _now);
        using var host = new LanDiscovery(port, () => _now);
        using var sender = new UdpClient();
        sender.EnableBroadcast = true;

        // Positive precondition: prove the transport actually works before
        // trusting the negative assertion below (Finding 1).
        var room = Sample();
        host.Announce(room);
        Settle(listener);
        Assert.Contains(listener.Rooms, r => r.Id == room.Id);

        var junk = new byte[32];
        Random.Shared.NextBytes(junk);
        sender.Send(junk, junk.Length, new IPEndPoint(IPAddress.Broadcast, port));

        for (int i = 0; i < 20; i++) { listener.Tick(); Thread.Sleep(5); }

        Assert.Single(listener.Rooms); // the garbage didn't add anything
    }

    [Fact]
    public void Reports_a_persistent_send_failure()
    {
        const int port = BasePort + 6;
        using var discovery = new LanDiscovery(port, () => _now);

        Assert.Null(discovery.LastSendFailure);

        BreakSending(discovery);
        discovery.Announce(Sample());

        Assert.NotNull(discovery.LastSendFailure);
    }

    [Fact]
    public void Caps_datagrams_processed_per_tick()
    {
        const int port = BasePort + 7;
        using var listener = new LanDiscovery(port, () => _now);
        using var sender = new UdpClient { EnableBroadcast = true };

        int flood = LanDiscovery.MaxDatagramsPerTick + 20;
        for (int i = 0; i < flood; i++)
        {
            var data = RoomState.Serialise(Sample($"room-{i}"));
            sender.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, port));
        }

        // Give the OS time to queue all of them before the single Tick() below.
        Thread.Sleep(300);

        listener.Tick();

        Assert.True(listener.Rooms.Count <= LanDiscovery.MaxDatagramsPerTick,
            $"expected at most {LanDiscovery.MaxDatagramsPerTick} rooms processed in a single Tick, got {listener.Rooms.Count}");
    }

    [Fact]
    public void Safe_to_use_after_dispose()
    {
        const int port = BasePort + 8;
        var discovery = new LanDiscovery(port, () => _now);
        discovery.Dispose();

        var ex = Record.Exception(() =>
        {
            discovery.Tick();
            discovery.Announce(Sample());
            _ = discovery.Rooms;
            discovery.Dispose(); // disposing twice must also not throw
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Distinguishes_no_local_room_from_a_zero_id_room()
    {
        const int port = BasePort + 9;
        using var listener = new LanDiscovery(port, () => _now); // LocalRoomId left unset
        using var sender = new UdpClient();
        sender.EnableBroadcast = true;

        var zeroIdRoom = new Room(Guid.Empty, "Zero id room", "Trial Mountain", 6, [new Player("ian", "", false)]);
        var data = RoomState.Serialise(zeroIdRoom);
        sender.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, port));

        Settle(listener);

        Assert.Contains(listener.Rooms, r => r.Id == Guid.Empty);
    }

    // ---- Finding 3: TryGetHostAddress must honour its own contract ----

    [Fact]
    public void TryGetHostAddress_returns_the_address_a_known_room_announced_from()
    {
        const int port = BasePort + 10;
        using var listener = new LanDiscovery(port, () => _now);
        using var host = new LanDiscovery(port, () => _now);

        var room = Sample();
        host.Announce(room);
        Settle(listener);

        // Broadcast on this machine arrives tagged with whatever local
        // interface address the OS picked for it, not necessarily loopback
        // - so assert it's a real address (not the IPAddress.None sentinel
        // TryGetHostAddress returns for the false case), not a specific one.
        Assert.True(listener.TryGetHostAddress(room.Id, out var address));
        Assert.NotEqual(IPAddress.None, address);
    }

    [Fact]
    public void TryGetHostAddress_returns_false_for_a_room_that_has_never_been_seen()
    {
        const int port = BasePort + 11;
        using var listener = new LanDiscovery(port, () => _now);

        Assert.False(listener.TryGetHostAddress(Guid.NewGuid(), out _));
    }

    [Fact]
    public void TryGetHostAddress_returns_false_once_a_room_has_aged_out_even_before_the_next_Tick()
    {
        const int port = BasePort + 12;
        using var listener = new LanDiscovery(port, () => _now);
        using var host = new LanDiscovery(port, () => _now);

        var room = Sample();
        host.Announce(room);
        Settle(listener);
        Assert.True(listener.TryGetHostAddress(room.Id, out _));

        // Past the timeout, but Tick()/Expire() deliberately not called - the
        // entry is still sitting in _seen, unswept. The contract promises
        // false regardless of whether a sweep has happened yet (Finding 3).
        Advance(LanDiscovery.Timeout.TotalSeconds + 0.001);

        Assert.False(listener.TryGetHostAddress(room.Id, out _));
    }

    [Fact]
    public void TryGetHostAddress_returns_false_after_dispose()
    {
        const int port = BasePort + 13;
        var listener = new LanDiscovery(port, () => _now);
        using var host = new LanDiscovery(port, () => _now);

        var room = Sample();
        host.Announce(room);
        Settle(listener);
        Assert.True(listener.TryGetHostAddress(room.Id, out _));

        listener.Dispose();

        Assert.False(listener.TryGetHostAddress(room.Id, out _));
    }
}
