using System.Net;
using System.Net.Sockets;
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class LanSessionTests
{
    // Well clear of LanDiscoveryTests' BasePort+0..13 (34800-34813) and of the
    // extra LanDiscovery/LanSession ports SessionTests binds (34740, 34741,
    // 34742, 34750, 34751, 34752) - see those files for why each test needs
    // its own port. Offsets below run through BasePort+24.
    const int BasePort = 34760;

    DateTime _now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
    void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    static LanSession.ClientIntent SampleIntent(bool ready, bool leaving) =>
        new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "guest", "Skyline GT-R", ready, leaving);

    [Fact]
    public void Round_trips_the_paint_a_client_chose()
    {
        var intent = SampleIntent(ready: true, leaving: false) with { Colour = 9 };

        Assert.True(LanSession.TryDeserialise(LanSession.Serialise(intent), out var back));

        Assert.Equal((byte)9, back.Colour);
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

    // ---- ForHost / ForClient ----

    [Fact]
    public void ForClient_binds_a_port_that_is_neither_0_nor_the_host_port()
    {
        const int hostPort = BasePort + 12;

        // Finding 8: hold the host port with a real ForHost first. Without
        // this, hostPort is never bound by anything and sits below
        // Windows' ephemeral range, so the OS-assigned client port could
        // never land on it anyway - the negative assertion below would be
        // unfalsifiable. Binding it for real is both falsifiable and the
        // production arrangement.
        using var host = LanSession.ForHost(hostPort, () => _now);
        using var client = LanSession.ForClient(hostPort, () => _now);

        Assert.NotEqual(0, client.BoundPort);
        Assert.NotEqual(hostPort, client.BoundPort);
    }

    [Fact]
    public void Two_ForClient_instances_at_once_each_get_their_own_port()
    {
        const int hostPort = BasePort + 13;
        using var a = LanSession.ForClient(hostPort, () => _now);
        using var b = LanSession.ForClient(hostPort, () => _now);

        Assert.NotEqual(a.BoundPort, b.BoundPort);
    }

    [Fact]
    public void ForHost_on_a_port_already_held_by_another_ForHost_throws()
    {
        const int port = BasePort + 14;
        using var first = LanSession.ForHost(port, () => _now);

        // Positive precondition: prove the port was actually claimed before
        // trusting that a second bind on it fails.
        Assert.Equal(port, first.BoundPort);

        Assert.Throws<SocketException>(() => LanSession.ForHost(port, () => _now));
    }

    // ---- host and client on loopback ----
    //
    // A genuine round trip needs two live LanSession sockets, each able to
    // both send and receive. Now that host and client bind differently -
    // the host a fixed port, the client an OS-assigned ephemeral one - two
    // real instances in one process can finally talk to each other without
    // depending on undefined "first binder wins" delivery behaviour. Most
    // of the tests below still pair a real LanSession under test with a
    // plain UdpClient standing in for the other side (exercising the exact
    // bytes LanSession puts on the wire via the internal
    // Serialise/TryDeserialise it shares with the class under test), which
    // keeps each one focused on a single method. The interop test further
    // down is the exception: it drives two real instances together, because
    // that is the one thing no single-instance test can prove.

    [Fact]
    public void HostTick_applies_a_clients_intent_and_replies_with_the_room_state_after_applying()
    {
        const int port = BasePort + 4;
        var hostSession = new Session("ian", () => _now);
        hostSession.Host("Ian's room", "Trial Mountain", "special");
        using var host = LanSession.ForHost(port, () => _now);

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
        const int hostPort = BasePort + 5;
        var clientSession = new Session("guest", () => _now);
        var initialRoom = new Room(Guid.NewGuid(), "Ian's room", "Trial Mountain", "special", RoomState.MaxPlayers,
            [new Player("ian", "", false)]);
        Assert.True(clientSession.Join(initialRoom));

        // Positive precondition (Finding 1): Join alone already puts "ian"
        // and "guest" (added locally) in Current, so asserting on either of
        // those would pass even if ClientTick's body were a no-op. Confirm
        // that starting point explicitly, then assert on a player that can
        // only appear once the host's reply below is actually ingested.
        Assert.Equal(2, clientSession.Current!.Players.Count);

        using var client = LanSession.ForClient(hostPort, () => _now);

        // The whole-room reply the host would have sent back: same room id,
        // plus a third player the client has no way of knowing about except
        // by ClientTick reading this datagram off the wire.
        var updatedRoom = clientSession.Current with
        {
            Players = [.. clientSession.Current.Players, new Player("stranger", "car", false)],
        };
        using (var rawHost = new UdpClient())
        {
            var data = RoomState.Serialise(updatedRoom);
            rawHost.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, client.BoundPort));
        }

        for (int i = 0; i < 50 && !clientSession.Current!.Players.Any(p => p.Name == "stranger"); i++)
        {
            client.ClientTick(clientSession, IPAddress.Loopback);
            Thread.Sleep(10);
        }

        Assert.Contains(clientSession.Current!.Players, p => p.Name == "ian");
        Assert.Contains(clientSession.Current.Players, p => p.Name == "guest");
        Assert.Contains(clientSession.Current.Players, p => p.Name == "stranger"); // only ClientTick could have added this
    }

    [Fact]
    public void HostTick_ignores_a_client_message_for_a_different_room_id()
    {
        const int port = BasePort + 6;
        var hostSession = new Session("ian", () => _now);
        hostSession.Host("room", "track", "special");
        using var host = LanSession.ForHost(port, () => _now);
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

    // Finding 5: the previous version of this coverage put two real
    // LanSession instances on the identical port (host bound first "to
    // receive", client bound second "to only ever send") and relied on this
    // machine's particular delivery-to-the-first-binder behaviour to keep
    // the client's own drain loop from stealing the intent meant for the
    // host. That is undefined by the socket API, not guaranteed by anything
    // in LanSession, and the comment said so. Split into two tests that each
    // follow the file's established one-real-instance-plus-a-stand-in
    // pattern instead: the first proves SendLeave puts a correctly addressed
    // Leaving datagram on the wire, received here by a plain UdpClient bound
    // to the host's well-known port standing in for the host; the second
    // proves HostTick treats a Leaving datagram as a departure, the same way
    // HostTick_applies_a_clients_intent... proves it treats a non-leaving
    // one as an update.

    [Fact]
    public void SendLeave_sends_a_leaving_flagged_intent_addressed_to_the_host_port()
    {
        const int hostPort = BasePort + 7;
        var hostRoom = new Room(Guid.NewGuid(), "room", "track", "special", RoomState.MaxPlayers, [new Player("ian", "", false)]);
        var clientSession = new Session("guest", () => _now);
        Assert.True(clientSession.Join(hostRoom));
        clientSession.SetCar("guest", "Supra");
        clientSession.SetReady("guest", true);

        using var rawHost = new UdpClient(new IPEndPoint(IPAddress.Loopback, hostPort)) { Client = { ReceiveTimeout = 2000 } };
        using var client = LanSession.ForClient(hostPort, () => _now);

        client.SendLeave(clientSession, IPAddress.Loopback);

        IPEndPoint? from = null;
        var data = rawHost.Receive(ref from);

        Assert.True(LanSession.TryDeserialise(data, out var intent));
        Assert.True(intent.Leaving);
        Assert.Equal(hostRoom.Id, intent.RoomId);
        Assert.Equal("guest", intent.Name);
        Assert.Equal("Supra", intent.Car);
        Assert.True(intent.Ready);
    }

    [Fact]
    public void HostTick_applies_a_leaving_intent_as_a_departure()
    {
        const int port = BasePort + 10;
        var hostSession = new Session("ian", () => _now);
        hostSession.Host("room", "track", "special");
        using var host = LanSession.ForHost(port, () => _now);
        using var rawClient = new UdpClient { Client = { ReceiveTimeout = 2000 } };

        // Positive precondition (Finding 1 applies here too): prove the
        // guest is actually in the room before trusting the negative
        // assertion below.
        var joinIntent = new LanSession.ClientIntent(hostSession.Current!.Id, "guest", "Supra", true, false);
        var joinData = LanSession.Serialise(joinIntent);
        rawClient.Send(joinData, joinData.Length, new IPEndPoint(IPAddress.Loopback, port));
        for (int i = 0; i < 50 && !hostSession.Current!.Players.Any(p => p.Name == "guest"); i++)
        {
            host.HostTick(hostSession);
            Thread.Sleep(10);
        }
        Assert.Contains(hostSession.Current!.Players, p => p.Name == "guest");

        var leaveIntent = new LanSession.ClientIntent(hostSession.Current!.Id, "guest", "Supra", true, true);
        var leaveData = LanSession.Serialise(leaveIntent);
        rawClient.Send(leaveData, leaveData.Length, new IPEndPoint(IPAddress.Loopback, port));

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
        const int hostPort = BasePort + 8;
        var clientSession = new Session("guest", () => _now);
        var room = new Room(Guid.NewGuid(), "room", "track", "special", RoomState.MaxPlayers, [new Player("ian", "", false)]);
        Assert.True(clientSession.Join(room));

        using var rawHost = new UdpClient(new IPEndPoint(IPAddress.Loopback, hostPort)) { Client = { ReceiveTimeout = 1 } };
        using var client = LanSession.ForClient(hostPort, () => _now);

        int CountAndDrainSelfSent()
        {
            Thread.Sleep(30); // give a loopback send time to land
            int count = 0;
            while (rawHost.Available > 0)
            {
                IPEndPoint? from = null;
                rawHost.Receive(ref from);
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
        var session = LanSession.ForHost(port, () => _now);
        session.Dispose();

        var hostSession = new Session("ian", () => _now);
        hostSession.Host("room", "track", "special");
        var clientSession = new Session("guest", () => _now);
        clientSession.Join(hostSession.Current!);

        var ex = Record.Exception(() =>
        {
            session.HostTick(hostSession);
            session.ClientTick(clientSession, IPAddress.Loopback);
            session.SendLeave(clientSession, IPAddress.Loopback);
            _ = session.LastSendFailure;
            _ = session.BoundPort; // Finding 6: must return quietly, like every other member here
            session.Dispose(); // disposing twice must also not throw
        });

        Assert.Null(ex);
    }

    // ---- real host and real client interoperating in one process ----

    [Fact]
    public void Two_real_LanSession_instances_interoperate_over_loopback()
    {
        const int hostPort = BasePort + 11;

        var hostSession = new Session("ian", () => _now);
        hostSession.Host("Ian's room", "Trial Mountain", "special");
        using var host = LanSession.ForHost(hostPort, () => _now);

        var clientSession = new Session("guest", () => _now);
        Assert.True(clientSession.Join(hostSession.Current!));
        Assert.Equal("special", clientSession.Current!.CarGroup);     // carried by Join itself
        using var client = LanSession.ForClient(hostPort, () => _now);

        // Finding 1: a value set on the host's own row, after the client
        // already joined locally, so the client can only ever come to know
        // it by actually receiving HostTick's reply over the wire and
        // ingesting it via ClientTick's OnRemoteState - Join, above, ran
        // before this was set, so it cannot have carried it in.
        hostSession.SetCar("ian", "R32 GT-R");

        // Positive precondition: confirm the client does not already know
        // this before a single datagram has been exchanged.
        Assert.DoesNotContain(clientSession.Current!.Players, p => p.Name == "ian" && p.Car == "R32 GT-R");

        bool DriveUntil(Func<bool> condition)
        {
            for (int i = 0; i < 200 && !condition(); i++)
            {
                host.HostTick(hostSession);
                client.ClientTick(clientSession, IPAddress.Loopback);
                Advance(0.25); // clears ClientTick's own five-times-a-second limit every iteration
                Thread.Sleep(5);
            }
            return condition();
        }

        Assert.True(DriveUntil(() =>
            clientSession.Current!.Players.Any(p => p.Name == "ian" && p.Car == "R32 GT-R")));

        Assert.Contains(hostSession.Current!.Players, p => p.Name == "ian");
        Assert.Contains(hostSession.Current.Players, p => p.Name == "guest");
        Assert.Contains(clientSession.Current!.Players, p => p.Name == "ian");
        Assert.Contains(clientSession.Current.Players, p => p.Name == "guest");

        // Carried by the actual wire round trip now, not merely by the local
        // Join above: every HostTick reply the client has ingested by this
        // point still says "special".
        Assert.Equal("special", clientSession.Current.CarGroup);

        // CanStart needs every player ready, including the host. The host
        // readies itself locally - it owns its own row and there's no wire
        // involved in that - while the client's readiness has to travel
        // over the channel under test, which is the point of this test.
        hostSession.SetReady("ian", true);
        clientSession.SetReady("guest", true);

        Assert.True(DriveUntil(() => hostSession.CanStart));
    }

    // ---- the start barrier ----
    //
    // The barrier runs after the lobby has exited, and nothing calls ClientTick
    // there. A client that learned of the start only through ClientTick would
    // wait for a flag nobody could raise, sit out its whole patience and start
    // alone - which is exactly what two instances did.
    //
    // The barrier's own messages are separate from the lobby's on purpose. They
    // were not, and a measured run of two machines shows the price: the client
    // arrived carrying the lobby's "the race is on", released itself one
    // millisecond later, and the host - 2.1s behind - was satisfied by the one
    // report that client had sent on its way past. The tests below hold each
    // half of that apart.

    /// <summary>The host's "the lobby is over", written out rather than shared.</summary>
    static readonly byte[] LeaveTheLobbyDatagram = [0xA5, 2];

    /// <summary>The host's "everyone is at the line, begin".</summary>
    static readonly byte[] StartTheRaceDatagram = [0xA5, 4];

    /// <summary>A player's "I have the race loaded and am holding".</summary>
    static readonly byte[] AtTheLineDatagram = [0xA5, 1];

    [Fact]
    public void A_client_holding_at_the_line_hears_the_hosts_start()
    {
        const int hostPort = BasePort + 15;
        using var client = LanSession.ForClient(hostPort, () => _now);
        using var host = new UdpClient(0);

        Assert.False(client.HostSaidStartTheRace);

        Deliver(host, client, StartTheRaceDatagram);
        client.CollectTheStart();

        Assert.True(client.HostSaidStartTheRace);
    }

    [Fact]
    public void The_message_that_ended_the_lobby_does_not_release_the_line()
    {
        const int hostPort = BasePort + 16;
        using var client = LanSession.ForClient(hostPort, () => _now);
        using var host = new UdpClient(0);

        // This is the one that used to. It is the host saying the race is on
        // and the lobby is over, which every client has already heard by the
        // time it reaches a line - so a line satisfied by it is a line nobody
        // ever waits at.
        Deliver(host, client, LeaveTheLobbyDatagram);
        client.CollectTheStart();

        Assert.False(client.HostSaidStartTheRace);
    }

    [Fact]
    public void A_report_at_the_line_does_not_release_the_line()
    {
        const int hostPort = BasePort + 17;
        using var client = LanSession.ForClient(hostPort, () => _now);
        using var host = new UdpClient(0);

        // The other message on this channel, travelling the opposite way - a
        // client must not release itself on one.
        Deliver(host, client, AtTheLineDatagram);
        client.CollectTheStart();

        Assert.False(client.HostSaidStartTheRace);
    }

    [Fact]
    public void A_car_moving_does_not_release_the_line()
    {
        const int hostPort = BasePort + 18;
        using var client = LanSession.ForClient(hostPort, () => _now);
        using var host = new UdpClient(0);

        // A place carries the same magic and the code next to the start's, so
        // the only thing keeping them apart is that a place is twenty-one bytes
        // and a control message is two. A machine already released and driving
        // must not release the ones still holding.
        var place = new byte[21];
        place[0] = 0xA5;
        place[1] = 3;
        Deliver(host, client, place);
        client.CollectTheStart();

        Assert.False(client.HostSaidStartTheRace);
    }

    [Fact]
    public void Opening_a_line_forgets_a_start_already_heard()
    {
        const int hostPort = BasePort + 19;
        using var client = LanSession.ForClient(hostPort, () => _now);
        using var host = new UdpClient(0);

        Deliver(host, client, StartTheRaceDatagram);
        client.CollectTheStart();
        Assert.True(client.HostSaidStartTheRace);

        // A second race in the same session gets its own line. The start that
        // released the first one says nothing about this one.
        client.OpenTheStartLine();

        Assert.False(client.HostSaidStartTheRace);
    }

    [Fact]
    public void Opening_a_line_drops_what_was_already_queued()
    {
        const int hostPort = BasePort + 20;
        using var client = LanSession.ForClient(hostPort, () => _now);
        using var host = new UdpClient(0);

        // Arrived before the line opened, and still sitting in the socket:
        // clearing the flag alone would leave the first collect to undo it.
        Deliver(host, client, StartTheRaceDatagram);
        client.OpenTheStartLine();
        client.CollectTheStart();

        Assert.False(client.HostSaidStartTheRace);
    }

    [Fact]
    public void Opening_a_line_forgets_who_was_at_the_previous_one()
    {
        const int hostPort = BasePort + 21;
        using var host = LanSession.ForHost(hostPort, () => _now);
        using var player = new UdpClient(0);

        // The host counts itself, so one report makes two.
        Assert.Equal(1, host.WaitingAtTheLine);

        Deliver(player, host, AtTheLineDatagram);
        host.CollectAtTheLine();
        Assert.Equal(2, host.WaitingAtTheLine);

        // A player at the previous race's line is not thereby at this one's.
        // The host believing otherwise is the half of the failure that let it
        // start on a report sent 2.1 seconds earlier.
        host.OpenTheStartLine();

        Assert.Equal(1, host.WaitingAtTheLine);
    }

    /// <summary>Sends one datagram and waits for it to actually arrive.</summary>
    static void Deliver(UdpClient from, LanSession to, byte[] data)
    {
        from.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, to.BoundPort));
        WaitForDelivery(to);
    }

    /// <summary>
    /// Loopback delivery is not instant, and a receive that finds nothing is
    /// indistinguishable from one that found the wrong thing - which would
    /// make the negative test above pass for the wrong reason.
    /// </summary>
    static void WaitForDelivery(LanSession session)
    {
        for (int i = 0; i < 200 && session.Available == 0; i++) Thread.Sleep(5);
        Assert.True(session.Available > 0, "the datagram never arrived");
    }

    // ---- passing a car on ----
    //
    // A client's socket knows one address, the host's. Two players therefore
    // work by accident: host and client are the only pair there is. Three do
    // not - the second client never hears the first, so that car is never given
    // a place and the game's own driver takes it over, which is what four
    // players on one screen looked like.

    /// <summary>
    /// A place message: the magic, the kind, a seat, three coordinates, three
    /// angles, and one angle per wheel.
    ///
    /// Sized from the real thing rather than written out as a number - a place
    /// shorter than the reader expects is dropped in silence, and building one
    /// by hand at the old width is exactly how that would go unnoticed.
    /// </summary>
    static byte[] PlaceFrom(byte seat)
    {
        var pose = new RemoteCars.Pose(
            new RemoteCars.Place(1234, 5678, 9012),
            AroundX: 11, AroundY: 22, AroundZ: 33,
            Wheels: new RemoteCars.Wheels(101, 202, 303, 404));

        using var sender = LanSession.ForClient(0, () => DateTime.UtcNow);
        using var listener = new UdpClient(0);
        var at = (IPEndPoint)listener.Client.LocalEndPoint!;
        sender.KnowsAbout(new IPEndPoint(IPAddress.Loopback, at.Port));
        sender.SendPlace(seat, pose);

        listener.Client.ReceiveTimeout = 1000;
        IPEndPoint? from = null;
        return listener.Receive(ref from);
    }

    /// <summary>
    /// The wheels ride along with the place, unchanged. They are the one thing
    /// a machine cannot work out about somebody else's car - the game derives a
    /// wheel's angle from the car's own physics, and a car this port teleports
    /// has none - so a place that arrived without them would leave four wheels
    /// turned to whatever the local guess was.
    /// </summary>
    [Fact]
    public void A_place_carries_what_the_wheels_are_doing()
    {
        const int hostPort = BasePort + 25;
        using var host = LanSession.ForHost(hostPort, () => _now);
        using var client = new UdpClient(0);

        var place = PlaceFrom(seat: 4);
        client.Send(place, place.Length, new IPEndPoint(IPAddress.Loopback, host.BoundPort));
        WaitForDelivery(host);
        host.CollectPlaces();

        Assert.Equal(
            new RemoteCars.Wheels(101, 202, 303, 404),
            host.Places[4].Wheels);
        Assert.Equal(new RemoteCars.Place(1234, 5678, 9012), host.Places[4].Place);
    }

    [Fact]
    public void A_host_passes_a_clients_place_on_to_the_other_clients()
    {
        const int hostPort = BasePort + 22;
        using var host = LanSession.ForHost(hostPort, () => _now);
        using var first = new UdpClient(0);
        using var second = new UdpClient(0);

        var firstAt = (IPEndPoint)first.Client.LocalEndPoint!;
        var secondAt = (IPEndPoint)second.Client.LocalEndPoint!;
        host.KnowsAbout(new IPEndPoint(IPAddress.Loopback, firstAt.Port));
        host.KnowsAbout(new IPEndPoint(IPAddress.Loopback, secondAt.Port));

        var place = PlaceFrom(seat: 1);
        first.Send(place, place.Length, new IPEndPoint(IPAddress.Loopback, host.BoundPort));
        WaitForDelivery(host);
        host.CollectPlaces();

        // The host took it for itself...
        Assert.True(host.Places.ContainsKey(1));

        // ...and the other client got it too, unchanged.
        IPEndPoint? from = null;
        second.Client.ReceiveTimeout = 1000;
        var got = second.Receive(ref from);
        Assert.Equal(place, got);
    }

    /// <summary>
    /// A result is final the moment it is sent, and is repeated only in case a
    /// datagram was lost - so the first report from a seat wins. Taking the
    /// latest instead would let a machine that has already gone back to the
    /// arcade overwrite a real result with whatever its memory then held.
    /// </summary>
    [Fact]
    public void The_first_result_from_a_seat_is_the_one_that_stands()
    {
        const int hostPort = BasePort + 26;
        using var host = LanSession.ForHost(hostPort, () => _now);
        using var client = LanSession.ForClient(hostPort, () => _now);

        var at = IPAddress.Loopback;
        client.SendResult(2, new RaceResult.Finish(3, 141456), at);
        WaitForDelivery(host);
        host.CollectResults();

        client.SendResult(2, new RaceResult.Finish(0, 7), at);
        WaitForDelivery(host);
        host.CollectResults();

        Assert.Equal(new RaceResult.Finish(3, 141456), host.Results[2]);
    }

    /// <summary>
    /// And the host passes one on, for the same reason it passes a place on: a
    /// client's socket knows only the host's address, so without the relay two
    /// clients never hear each other and each shows a table with a hole in it.
    /// </summary>
    [Fact]
    public void A_host_passes_a_result_on_to_the_other_clients()
    {
        const int hostPort = BasePort + 27;
        using var host = LanSession.ForHost(hostPort, () => _now);
        using var first = LanSession.ForClient(hostPort, () => _now);
        using var second = new UdpClient(0);

        var secondAt = (IPEndPoint)second.Client.LocalEndPoint!;
        host.KnowsAbout(new IPEndPoint(IPAddress.Loopback, secondAt.Port));

        first.SendResult(1, new RaceResult.Finish(2, 139002), IPAddress.Loopback);
        WaitForDelivery(host);
        host.CollectResults();

        Assert.Equal(new RaceResult.Finish(2, 139002), host.Results[1]);

        second.Client.ReceiveTimeout = 1000;
        IPEndPoint? from = null;
        var got = second.Receive(ref from);

        Assert.Equal(0xA5, got[0]);
        Assert.Equal(5, got[1]);
        Assert.Equal(1, got[2]);
        Assert.Equal(2, got[3]);
        Assert.Equal(139002, BitConverter.ToInt32(got, 4));
    }

    /// <summary>
    /// And not back to whoever sent it. A car does not need to be told where it
    /// is, and a room of six would otherwise spend a sixth of its traffic
    /// saying so.
    /// </summary>
    [Fact]
    public void A_place_is_not_passed_back_to_the_player_it_came_from()
    {
        const int hostPort = BasePort + 23;
        using var host = LanSession.ForHost(hostPort, () => _now);
        using var only = new UdpClient(0);

        var onlyAt = (IPEndPoint)only.Client.LocalEndPoint!;
        host.KnowsAbout(new IPEndPoint(IPAddress.Loopback, onlyAt.Port));

        var place = PlaceFrom(seat: 2);
        only.Send(place, place.Length, new IPEndPoint(IPAddress.Loopback, host.BoundPort));
        WaitForDelivery(host);
        host.CollectPlaces();

        only.Client.ReceiveTimeout = 200;
        Assert.Throws<SocketException>(() =>
        {
            IPEndPoint? from = null;
            only.Receive(ref from);
        });
    }

    /// <summary>
    /// A client passes nothing on. It has nobody to pass to, and a client that
    /// echoed what it received would multiply every place by the number of
    /// players in the room.
    /// </summary>
    [Fact]
    public void A_client_passes_nothing_on()
    {
        const int hostPort = BasePort + 24;
        using var client = LanSession.ForClient(hostPort, () => _now);
        using var other = new UdpClient(0);

        var otherAt = (IPEndPoint)other.Client.LocalEndPoint!;
        client.KnowsAbout(new IPEndPoint(IPAddress.Loopback, otherAt.Port));

        var place = PlaceFrom(seat: 3);
        other.Send(place, place.Length, new IPEndPoint(IPAddress.Loopback, client.BoundPort));
        WaitForDelivery(client);
        client.CollectPlaces();

        Assert.True(client.Places.ContainsKey(3));

        other.Client.ReceiveTimeout = 200;
        Assert.Throws<SocketException>(() =>
        {
            IPEndPoint? from = null;
            other.Receive(ref from);
        });
    }
}
