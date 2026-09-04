using System.Net;
using GT2Port.Multiplayer;
using GT2Port.Rendezvous;
using GT2Relay;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// A link that reaches a host through a server instead of directly.
///
/// Tested against the real relay on loopback. The claim being made is that
/// what a session sends comes out of the other session with the *peer* named
/// as the sender - not the relay - because that identity is what the lobby
/// keys players by, and a relay that hid it would make every player in a room
/// look like the same one.
/// </summary>
public class RelaySessionTests : IDisposable
{
    readonly RelayServer _server = new(0);
    DateTime _now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    IPEndPoint Where => new(IPAddress.Loopback, _server.BoundPort);
    RelaySession New() => new(Where, () => _now);
    void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    public void Dispose() => _server.Dispose();

    /// <summary>
    /// Pumps both sides for a while, because a datagram on loopback is fast
    /// but not instant and the server only answers when it is asked to.
    /// </summary>
    void Settle(params RelaySession[] sessions)
    {
        for (int i = 0; i < 40; i++)
        {
            _server.Pump();
            foreach (var session in sessions) _ = session.Available;
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// The relay names the host when it admits a player, and that is the only
    /// address a room found on the internet ever has: the client is not on the
    /// host's network, so it hears no announcement, and it typed nothing.
    /// Without reading this the client had nobody to speak to and was dropped
    /// for going quiet three seconds later.
    /// </summary>
    [Fact]
    public void A_client_learns_who_is_hosting_from_the_relay()
    {
        using var host = New();
        using var guest = New();
        var roomId = Guid.NewGuid();

        host.Publish(roomId, listed: true, [1, 2, 3]);
        Settle(host, guest);

        Assert.Null(guest.Host);

        guest.Join(roomId);
        Settle(host, guest);

        Assert.True(guest.Admitted);
        Assert.NotNull(guest.Host);
        Assert.Equal(host.BoundPort, guest.Host!.Port);
    }

    /// <summary>
    /// And a host learns nothing of the sort. It is sent a Peer for every
    /// player who joins, and none of them is its host - reading those the same
    /// way would have a host addressing its first guest as though it were the
    /// room's owner.
    /// </summary>
    [Fact]
    public void A_host_is_told_about_players_and_not_about_a_host()
    {
        using var host = New();
        using var guest = New();
        var roomId = Guid.NewGuid();

        host.Publish(roomId, listed: true, [1]);
        Settle(host, guest);
        guest.Join(roomId);
        Settle(host, guest);

        Assert.NotEmpty(host.Peers);
        Assert.Null(host.Host);
    }

    [Fact]
    public void Publishing_gets_a_code_back()
    {
        using var host = New();
        var roomId = Guid.NewGuid();

        host.Publish(roomId, listed: true, [1, 2, 3]);
        Settle(host);

        Assert.True(RoomCode.IsWellFormed(host.Code));
        Assert.Equal(roomId, host.RoomId);
    }

    [Fact]
    public void A_listed_room_turns_up_for_somebody_asking()
    {
        using var host = New();
        using var guest = New();
        var roomId = Guid.NewGuid();
        byte[] card = [9, 8, 7];

        host.Publish(roomId, listed: true, card);
        Settle(host);

        guest.AskForRooms();
        Settle(host, guest);

        var room = Assert.Single(guest.Rooms);
        Assert.Equal(roomId, room.Id);
        Assert.Equal(card, room.Card);
        Assert.Equal(host.Code, room.Code);
    }

    [Fact]
    public void An_unlisted_room_is_reached_by_its_code_and_not_by_the_list()
    {
        using var host = New();
        using var guest = New();
        var roomId = Guid.NewGuid();

        host.Publish(roomId, listed: false, [1]);
        Settle(host);

        guest.AskForRooms();
        Settle(host, guest);
        Assert.Empty(guest.Rooms);

        guest.JoinByCode(host.Code);
        Settle(host, guest);

        Assert.True(guest.Admitted);
        Assert.Equal(roomId, guest.RoomId);
    }

    [Fact]
    public void A_code_that_names_nothing_is_refused_rather_than_ignored()
    {
        using var guest = New();

        guest.JoinByCode("ZZZZZZ");
        Settle(guest);

        Assert.True(guest.Refused);
        Assert.False(guest.Admitted);
    }

    /// <summary>
    /// The whole point of the thing.
    /// </summary>
    [Fact]
    public void What_one_side_sends_arrives_at_the_other_with_the_peer_named()
    {
        using var host = New();
        using var guest = New();
        var roomId = Guid.NewGuid();

        host.Publish(roomId, listed: true, [1]);
        Settle(host);
        guest.Join(roomId);
        Settle(host, guest);

        var hostPeer = Assert.Single(guest.Peers);
        var guestPeer = Assert.Single(host.Peers);
        Assert.NotEqual(hostPeer, guestPeer);

        byte[] said = [0xA5, 1, 2, 3];
        guest.Send(said, said.Length, hostPeer);
        Settle(host, guest);

        Assert.True(host.Available > 0);
        IPEndPoint? from = null;
        var heard = host.Receive(ref from);

        Assert.Equal(said, heard);
        Assert.Equal(guestPeer, from);

        // And back the other way, addressed by what the host just learned.
        byte[] answered = [0xA5, 4, 5, 6];
        host.Send(answered, answered.Length, from!);
        Settle(host, guest);

        Assert.True(guest.Available > 0);
        IPEndPoint? backFrom = null;
        Assert.Equal(answered, guest.Receive(ref backFrom));
        Assert.Equal(hostPeer, backFrom);
    }

    /// <summary>
    /// Publishing is a keepalive on a timer, and a host that flooded the
    /// server with one publish per frame would be the first thing to get a
    /// relay rate-limited out of existence.
    /// </summary>
    [Fact]
    public void Publishing_more_often_than_the_interval_sends_nothing_extra()
    {
        using var host = New();
        var roomId = Guid.NewGuid();

        for (int i = 0; i < 50; i++) host.Publish(roomId, true, [1]);
        Settle(host);

        Assert.Equal(1, host.Published);

        Advance(RelaySession.PublishEvery.TotalSeconds + 0.1);
        host.Publish(roomId, true, [1]);
        Settle(host);

        Assert.Equal(2, host.Published);
    }

    /// <summary>
    /// A room whose host stopped publishing must leave the list, or the panel
    /// keeps offering a room nobody can join.
    /// </summary>
    [Fact]
    public void A_room_not_heard_of_lately_drops_out_of_the_list()
    {
        using var host = New();
        using var guest = New();

        host.Publish(Guid.NewGuid(), listed: true, [1]);
        Settle(host);
        guest.AskForRooms();
        Settle(host, guest);
        Assert.Single(guest.Rooms);

        Advance(RelaySession.Forgotten.TotalSeconds + 1);

        Assert.Empty(guest.Rooms);
    }

    [Fact]
    public void Rubbish_from_elsewhere_is_not_mistaken_for_game_traffic()
    {
        using var guest = New();
        using var stranger = new System.Net.Sockets.UdpClient(0);

        byte[] noise = [1, 2, 3, 4, 5];
        stranger.Send(noise, noise.Length,
            new IPEndPoint(IPAddress.Loopback, guest.BoundPort));

        Settle(guest);

        Assert.Equal(0, guest.Available);
    }
}
