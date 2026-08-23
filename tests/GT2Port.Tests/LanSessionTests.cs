using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class LanSessionTests
{
    // Well clear of LanDiscoveryTests' BasePort+0..9 (34719-34728) and of the
    // extra LanDiscovery/LanSession ports SessionTests binds (34740, 34741,
    // 34750, 34751) - see those files for why each test needs its own port.
    const int BasePort = 34760;

    DateTime _now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
    void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    static LanSession.ClientIntent SampleIntent(bool ready, bool leaving) =>
        new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "guest", "Skyline GT-R", ready, leaving);

    static UdpClient GetSocket(LanSession session)
    {
        var field = typeof(LanSession).GetField("_socket", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("LanSession no longer has a _socket field.");
        return (UdpClient)field.GetValue(session)!;
    }

    // ---- wire format ----

    [Fact]
    public void Round_trips_an_intent_with_both_flags_set()
    {
        var intent = SampleIntent(ready: true, leaving: true);
        Assert.True(LanSession.TryDeserialise(LanSession.Serialise(intent), out var back));

        Assert.Equal(intent.RoomId, back.RoomId);
        Assert.Equal(intent.Name, back.Name);
        Assert.Equal(intent.Car, back.Car);
        Assert.True(back.Ready);
        Assert.True(back.Leaving);
    }

    [Fact]
    public void Round_trips_an_intent_with_both_flags_clear()
    {
        var intent = SampleIntent(ready: false, leaving: false);
        Assert.True(LanSession.TryDeserialise(LanSession.Serialise(intent), out var back));

        Assert.Equal(intent.RoomId, back.RoomId);
        Assert.Equal(intent.Name, back.Name);
        Assert.Equal(intent.Car, back.Car);
        Assert.False(back.Ready);
        Assert.False(back.Leaving);
    }

    [Fact]
    public void Rejects_an_empty_packet_without_throwing()
    {
        Assert.False(LanSession.TryDeserialise([], out _));
    }

    [Fact]
    public void Rejects_a_truncated_packet_without_throwing()
    {
        var data = LanSession.Serialise(SampleIntent(true, false));
        Assert.False(LanSession.TryDeserialise(data[..(data.Length / 2)], out _));
    }

    [Fact]
    public void Rejects_the_wrong_magic_without_throwing()
    {
        var data = LanSession.Serialise(SampleIntent(true, false));
        data[0] = (byte)'X';
        Assert.False(LanSession.TryDeserialise(data, out _));
    }

    [Fact]
    public void Rejects_an_unknown_version_without_throwing()
    {
        var data = LanSession.Serialise(SampleIntent(true, false));
        data[4] = 99; // the byte right after the 4-byte magic
        Assert.False(LanSession.TryDeserialise(data, out _));
    }

    // ---- host and client on loopback ----
    //
    // A genuine round trip needs two live LanSession sockets, each able to
    // both send and receive, sharing the one well-known port every real peer
    // uses (matching production, and LanDiscoveryTests' convention of host
    // and listener sharing a port). On this machine, two sockets bound
    // wildcard to the identical port with ReuseAddress do not both receive
    // unicast traffic - only the first one bound ever does, regardless of
    // send order or destination address (verified experimentally: the
    // second-bound socket received zero of ten packets sent directly at it).
    // Broadcast is unaffected (both LanDiscoveryTests instances get every
    // broadcast), which is why that class doesn't hit this.
    //
    // So each direction below is tested with the real LanSession under test
    // on one side, and a plain UdpClient - bound to an unrelated ephemeral
    // port, never sharing the port under test - standing in for the other
    // side. That plain socket exercises the exact bytes LanSession puts on
    // the wire (via the internal Serialise/TryDeserialise it shares with the
    // class under test), so what's under test is genuinely this class's
    // HostTick/ClientTick behaviour, just not two live instances of it
    // sharing one OS port in one process.

    [Fact]
    public void HostTick_applies_a_clients_intent_and_replies_with_the_room_state_after_applying()
    {
        const int port = BasePort + 4;
        var hostSession = new Session("ian", () => _now);
        hostSession.Host("Ian's room", "Trial Mountain");
        using var host = new LanSession(port, () => _now);

        using var rawClient = new UdpClient { Client = { ReceiveTimeout = 2000 } };

        var intent = new LanSession.ClientIntent(hostSession.Current!.Id, "guest", "Supra", true, false);
        var data = LanSession.Serialise(intent);
        rawClient.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, port));

        Player? guest = null;
        for (int i = 0; i < 50 && guest is null; i++)
        {
            host.HostTick(hostSession);
            guest = hostSession.Current!.Players.SingleOrDefault(p => p.Name == "guest");
            if (guest is null) Thread.Sleep(10);
        }

        Assert.NotNull(guest);
        Assert.Equal("Supra", guest!.Car);
        Assert.True(guest.Ready);

        // The reply must be the room state as it stands after applying, sent
        // unicast back to the sender.
        IPEndPoint? from = null;
        var reply = rawClient.Receive(ref from);
        Assert.True(RoomState.TryDeserialise(reply, out var room));
        Assert.Contains(room.Players, p => p.Name == "guest" && p.Car == "Supra" && p.Ready);
    }

    [Fact]
    public void ClientTick_ingests_a_room_state_reply_into_the_session()
    {
        const int port = BasePort + 5;
        var clientSession = new Session("guest", () => _now);
        var initialRoom = new Room(Guid.NewGuid(), "Ian's room", "Trial Mountain", RoomState.MaxPlayers,
            [new Player("ian", "", false)]);
        Assert.True(clientSession.Join(initialRoom));

        using var client = new LanSession(port, () => _now);

        // The whole-room reply the host would have sent back after adding "guest".
        var updatedRoom = initialRoom with { Players = [.. initialRoom.Players, new Player("guest", "car", false)] };
        using (var rawHost = new UdpClient())
        {
            var data = RoomState.Serialise(updatedRoom);
            rawHost.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, port));
        }

        for (int i = 0; i < 50 && clientSession.Current!.Players.Count < 2; i++)
        {
            client.ClientTick(clientSession, IPAddress.Loopback);
            Thread.Sleep(10);
        }

        Assert.Contains(clientSession.Current!.Players, p => p.Name == "ian");
        Assert.Contains(clientSession.Current.Players, p => p.Name == "guest");
    }

    [Fact]
    public void HostTick_ignores_a_client_message_for_a_different_room_id()
    {
        const int port = BasePort + 6;
        var hostSession = new Session("ian", () => _now);
        hostSession.Host("room", "track");
        using var host = new LanSession(port, () => _now);
        using var rawClient = new UdpClient { Client = { ReceiveTimeout = 2000 } };

        // Positive precondition (Finding 1 from the discovery review applies
        // here too): prove the transport and HostTick actually work in this
        // test before trusting the negative assertion below.
        var correct = new LanSession.ClientIntent(hostSession.Current!.Id, "guest", "", false, false);
        var correctData = LanSession.Serialise(correct);
        rawClient.Send(correctData, correctData.Length, new IPEndPoint(IPAddress.Loopback, port));
        for (int i = 0; i < 50 && !hostSession.Current!.Players.Any(p => p.Name == "guest"); i++)
        {
            host.HostTick(hostSession);
            Thread.Sleep(10);
        }
        Assert.Contains(hostSession.Current!.Players, p => p.Name == "guest");

        var wrongRoom = new LanSession.ClientIntent(Guid.NewGuid(), "intruder", "", false, false);
        var wrongData = LanSession.Serialise(wrongRoom);
        rawClient.Send(wrongData, wrongData.Length, new IPEndPoint(IPAddress.Loopback, port));

        for (int i = 0; i < 20; i++) { host.HostTick(hostSession); Thread.Sleep(10); }

        Assert.DoesNotContain(hostSession.Current!.Players, p => p.Name == "intruder");
        Assert.Equal(2, hostSession.Current.Players.Count); // ian (host) + guest only
    }

    [Fact]
    public void SendLeave_is_applied_by_the_host_as_a_departure()
    {
        const int port = BasePort + 7;
        var hostSession = new Session("ian", () => _now);
        hostSession.Host("room", "track");
        using var host = new LanSession(port, () => _now); // bound first: this test needs it to receive

        var clientSession = new Session("guest", () => _now);
        Assert.True(clientSession.Join(hostSession.Current!));
        using var client = new LanSession(port, () => _now); // bound second: only ever sends in this test

        for (int i = 0; i < 50 && !hostSession.Current!.Players.Any(p => p.Name == "guest"); i++)
        {
            client.ClientTick(clientSession, IPAddress.Loopback);
            host.HostTick(hostSession);
            Thread.Sleep(10);
        }
        Assert.Contains(hostSession.Current!.Players, p => p.Name == "guest");

        client.SendLeave(clientSession, IPAddress.Loopback);

        for (int i = 0; i < 50 && hostSession.Current!.Players.Any(p => p.Name == "guest"); i++)
        {
            host.HostTick(hostSession);
            Thread.Sleep(10);
        }

        Assert.DoesNotContain(hostSession.Current!.Players, p => p.Name == "guest");
    }

    [Fact]
    public void ClientTick_sends_its_own_intent_no_more_than_five_times_a_second()
    {
        const int port = BasePort + 8;
        var clientSession = new Session("guest", () => _now);
        var room = new Room(Guid.NewGuid(), "room", "track", RoomState.MaxPlayers, [new Player("ian", "", false)]);
        Assert.True(clientSession.Join(room));

        using var client = new LanSession(port, () => _now);
        var socket = GetSocket(client);

        // hostAddress is loopback, and this client's own destination port
        // equals its own bind port, so its sends land right back in its own
        // receive queue - a convenient way to count them without a second
        // socket fighting for the same port.
        int CountAndDrainSelfSent()
        {
            Thread.Sleep(30); // give a loopback send time to land
            int count = 0;
            while (socket.Available > 0)
            {
                IPEndPoint? from = null;
                socket.Receive(ref from);
                count++;
            }
            return count;
        }

        client.ClientTick(clientSession, IPAddress.Loopback);
        Assert.Equal(1, CountAndDrainSelfSent()); // nothing sent yet, so this always sends

        client.ClientTick(clientSession, IPAddress.Loopback); // same instant: must not send again
        Assert.Equal(0, CountAndDrainSelfSent());

        Advance(0.1); // still under 1/5s since the first send
        client.ClientTick(clientSession, IPAddress.Loopback);
        Assert.Equal(0, CountAndDrainSelfSent());

        Advance(0.15); // 0.25s since the first send: past the interval
        client.ClientTick(clientSession, IPAddress.Loopback);
        Assert.Equal(1, CountAndDrainSelfSent());
    }

    [Fact]
    public void Safe_to_use_after_dispose()
    {
        const int port = BasePort + 9;
        var session = new LanSession(port, () => _now);
        session.Dispose();

        var hostSession = new Session("ian", () => _now);
        hostSession.Host("room", "track");
        var clientSession = new Session("guest", () => _now);
        clientSession.Join(hostSession.Current!);

        var ex = Record.Exception(() =>
        {
            session.HostTick(hostSession);
            session.ClientTick(clientSession, IPAddress.Loopback);
            session.SendLeave(clientSession, IPAddress.Loopback);
            _ = session.LastSendFailure;
            session.Dispose(); // disposing twice must also not throw
        });

        Assert.Null(ex);
    }
}
