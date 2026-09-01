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
    [InlineData(":34719")]
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
    /// talking to the host - and browsing still needs none. The role recorded
    /// for both is Joined, so being answered must not rebuild the socket
    /// underneath a client mid-handshake.
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
            ModeHook.SocketAction.Keep,
            ModeHook.DecideSocketAction(SessionPhase.Joined, SessionPhase.Joined));

        Assert.Equal(
            ModeHook.SocketAction.Drop,
            ModeHook.DecideSocketAction(SessionPhase.Joined, SessionPhase.Browsing));
    }
}
