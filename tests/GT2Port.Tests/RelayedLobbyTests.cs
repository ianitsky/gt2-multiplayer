using System.Net;
using GT2Port.Multiplayer;
using GT2Relay;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// A lobby where neither side can address the other.
///
/// Every other test of the lobby has the client send to the host's address.
/// Here there is no such address: both sides speak only to the relay, which is
/// the situation on the internet and the situation this whole stage exists
/// for. Nothing about the lobby itself is new - the point is that none of it
/// had to change.
/// </summary>
public class RelayedLobbyTests : IDisposable
{
    readonly RelayServer _server = new(0);
    DateTime _now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _server.Dispose();

    IPEndPoint Where => new(IPAddress.Loopback, _server.BoundPort);

    /// <summary>
    /// Runs the server and both links for a while. Both links, every pass:
    /// a link only takes in what it is asked to read, and a test that pumped
    /// only the server would watch two sessions ignore every answer.
    /// </summary>
    void Turn(RelaySession host, RelaySession guest, params Action[] work)
    {
        for (int i = 0; i < 60; i++)
        {
            _server.Pump();
            host.Tick();
            guest.Tick();
            foreach (var step in work) step();
            _now = _now.AddMilliseconds(50);
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void A_client_joins_a_host_it_has_no_route_to()
    {
        var hostSession = new Session("ian", () => _now);
        hostSession.Host("ian's room", "seattle_short", "special", 6);

        var guestSession = new Session("les", () => _now);

        using var hostRelay = new RelaySession(Where, () => _now);
        using var guestRelay = new RelaySession(Where, () => _now);

        using var hostWire = LanSession.Over(hostRelay, 34719, () => _now,
            hosting: true, ownsTheLink: false);
        using var guestWire = LanSession.Over(guestRelay, 34719, () => _now,
            hosting: false, ownsTheLink: false);

        var room = hostSession.Current!;

        // The host announces, the guest lists, the guest joins - all of it
        // through the relay, which is the only address either side has.
        Turn(hostRelay, guestRelay,
            () => hostRelay.Publish(room.Id, listed: true, RoomState.Serialise(room)),
            () => guestRelay.AskForRooms());

        var advert = Assert.Single(guestRelay.Rooms);
        Assert.Equal(room.Id, advert.Id);
        Assert.True(RoomState.TryDeserialise(advert.Card, out var seen));
        Assert.Equal("ian's room", seen.Name);

        guestRelay.Join(room.Id);
        Turn(hostRelay, guestRelay);
        Assert.True(guestRelay.Admitted);

        guestSession.Join(seen);

        // From here it is the ordinary lobby, over a link that happens to be
        // relayed. The host's address is whatever the relay told the guest.
        var hostEndPoint = Assert.Single(guestRelay.Peers);

        Turn(hostRelay, guestRelay,
            () => guestWire.ClientTick(guestSession, hostEndPoint.Address),
            () => hostWire.HostTick(hostSession));

        Assert.Contains(hostSession.Current!.Players, p => p.Name == "les");
        Assert.Equal(SessionPhase.Joined, guestSession.Phase);
        Assert.Equal(2, guestSession.Current!.Players.Count);
    }

    /// <summary>
    /// And by code, which is the path for a room that was never listed - the
    /// one somebody pastes into a chat.
    /// </summary>
    [Fact]
    public void A_private_room_is_reached_by_its_code()
    {
        var hostSession = new Session("ian", () => _now);
        hostSession.Host("ian's room", "seattle_short", "special", 6, secret: "hunter2");

        using var hostRelay = new RelaySession(Where, () => _now);
        using var guestRelay = new RelaySession(Where, () => _now);

        var room = hostSession.Current!;

        Turn(hostRelay, guestRelay,
            () => hostRelay.Publish(room.Id, listed: false, RoomState.Serialise(room)));

        Assert.NotEqual("", hostRelay.Code);

        guestRelay.AskForRooms();
        Turn(hostRelay, guestRelay);
        Assert.Empty(guestRelay.Rooms);

        guestRelay.JoinByCode(hostRelay.Code);
        Turn(hostRelay, guestRelay);

        Assert.True(guestRelay.Admitted);
        Assert.Equal(room.Id, guestRelay.RoomId);
    }
}
