using System.Net;
using System.Net.Sockets;
using GT2Port.Rendezvous;
using Xunit;

namespace GT2Relay.Tests;

/// <summary>
/// The registry's decisions, this time over a real socket.
///
/// Loopback rather than a mock: the thing being tested is precisely that
/// datagrams go out of the right socket to the right address, and a fake
/// socket would be a test of the fake. Port 0 so the OS picks, and the server
/// says which it got - two of these running at once must not collide.
/// </summary>
public class RelayServerTests
{
    /// <summary>
    /// A datagram put on the wire is not a datagram delivered. Loopback is
    /// fast but not instant, so this waits for the socket to say it has
    /// something rather than sleeping a guessed amount.
    /// </summary>
    static byte[] WaitFor(UdpClient socket, Func<byte[], bool> wanted)
    {
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
        {
            while (socket.Available > 0)
            {
                IPEndPoint? from = null;
                var data = socket.Receive(ref from);
                if (wanted(data)) return data;
            }
            Thread.Sleep(5);
        }
        Assert.Fail("nothing that was being waited for arrived within two seconds");
        return [];
    }

    static UdpClient Player()
    {
        var socket = new UdpClient(0);
        socket.Client.ReceiveTimeout = 500;
        return socket;
    }

    static void Send(UdpClient from, RelayServer server, byte[] data) =>
        from.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, server.BoundPort));

    static bool Kind(byte[] data, Envelope.Kind kind) =>
        Envelope.TryReadKind(data, out var got) && got == kind;

    [Fact]
    public void A_host_publishes_and_a_client_finds_the_room_and_they_talk()
    {
        using var server = new RelayServer(0);
        using var host = Player();
        using var guest = Player();

        var roomId = Guid.NewGuid();
        byte[] card = [42, 43, 44];

        Send(host, server, Envelope.WritePublish(roomId, listed: true, card));
        server.Pump();

        var published = WaitFor(host, d => Kind(d, Envelope.Kind.Published));
        Assert.True(Envelope.TryReadPublished(published, out _, out string code));
        Assert.True(RoomCode.IsWellFormed(code));

        Send(guest, server, Envelope.WriteList());
        server.Pump();

        var listed = WaitFor(guest, d => Kind(d, Envelope.Kind.RoomCard));
        Assert.True(Envelope.TryReadRoomCard(listed, out var listedId, out _, out var listedCard));
        Assert.Equal(roomId, listedId);
        Assert.Equal(card, listedCard);

        Send(guest, server, Envelope.WriteJoin(roomId));
        server.Pump();

        var peerForGuest = WaitFor(guest, d => Kind(d, Envelope.Kind.Peer));
        Assert.True(Envelope.TryReadPeer(peerForGuest, out _, out var hostEndPoint));

        var peerForHost = WaitFor(host, d => Kind(d, Envelope.Kind.Peer));
        Assert.True(Envelope.TryReadPeer(peerForHost, out _, out var guestEndPoint));

        // What the two sides now know about each other is what the relay saw,
        // which on loopback is the port each socket bound.
        Assert.Equal(((IPEndPoint)host.Client.LocalEndPoint!).Port, hostEndPoint.Port);
        Assert.Equal(((IPEndPoint)guest.Client.LocalEndPoint!).Port, guestEndPoint.Port);

        byte[] payload = [0xA5, 9, 8, 7];
        Send(guest, server, Envelope.WriteRelay(roomId, hostEndPoint, payload));
        server.Pump();

        var arrived = WaitFor(host, d => Kind(d, Envelope.Kind.Relayed));
        Assert.True(Envelope.TryReadRelayed(arrived, out _, out var from, out var got));
        Assert.Equal(guestEndPoint, from);
        Assert.Equal(payload, got);
    }

    [Fact]
    public void A_code_reaches_a_room_that_was_never_listed()
    {
        using var server = new RelayServer(0);
        using var host = Player();
        using var guest = Player();

        var roomId = Guid.NewGuid();
        Send(host, server, Envelope.WritePublish(roomId, listed: false, [1]));
        server.Pump();

        var published = WaitFor(host, d => Kind(d, Envelope.Kind.Published));
        Assert.True(Envelope.TryReadPublished(published, out _, out string code));

        Send(guest, server, Envelope.WriteList());
        server.Pump();
        Assert.Equal(0, guest.Available);

        Send(guest, server, Envelope.WriteJoinByCode(code));
        server.Pump();

        var joined = WaitFor(guest, d => Kind(d, Envelope.Kind.Joined));
        Assert.True(Envelope.TryReadJoined(joined, out var joinedId));
        Assert.Equal(roomId, joinedId);
    }

    /// <summary>
    /// A server that falls over on a malformed datagram is a server anybody
    /// can turn off with one packet.
    /// </summary>
    [Fact]
    public void Rubbish_does_not_stop_it_serving()
    {
        using var server = new RelayServer(0);
        using var noise = Player();
        using var host = Player();

        Send(noise, server, [0, 1, 2, 3, 4, 5]);
        Send(noise, server, []);
        Send(noise, server, [Envelope.Magic, 200, 200, 200]);
        server.Pump();

        Send(host, server, Envelope.WritePublish(Guid.NewGuid(), true, [1]));
        server.Pump();

        WaitFor(host, d => Kind(d, Envelope.Kind.Published));
    }

    [Fact]
    public void It_says_which_port_it_got()
    {
        using var server = new RelayServer(0);

        Assert.True(server.BoundPort > 0);
    }

    /// <summary>
    /// What was heard and what was answered, kept apart.
    ///
    /// The two ways a tunnelled relay fails look the same from a player's
    /// screen - an empty room list either way - and only these tell them
    /// apart: nothing arriving is one fault, everything arriving and being
    /// answered is a different one, outside this process entirely.
    /// </summary>
    [Fact]
    public void It_counts_what_it_heard_and_what_it_answered()
    {
        using var server = new RelayServer(0);
        using var host = Player();

        Assert.Equal(0, server.Received);
        Assert.Equal(0, server.Sent);
        Assert.Null(server.LastHeardFrom);

        Send(host, server, Envelope.WritePublish(Guid.NewGuid(), true, [1]));
        server.Pump();
        WaitFor(host, d => Kind(d, Envelope.Kind.Published));

        Assert.Equal(1, server.Received);
        Assert.Equal(1, server.Sent);
        Assert.Equal(((IPEndPoint)host.Client.LocalEndPoint!).Port, server.LastHeardFrom!.Port);

        // Heard and not worth answering, which is the case that has to move
        // one counter and not the other.
        Send(host, server, [0, 1, 2, 3]);
        server.Pump();

        Assert.Equal(2, server.Received);
        Assert.Equal(1, server.Sent);
    }
}
