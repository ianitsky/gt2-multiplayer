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

    /// <summary>
    /// And the client reads the answer.
    ///
    /// Every other test here stops at the host: the knock is sent, the secret
    /// is checked, the room takes the player. None of them ever asked whether
    /// the client hears the room state that comes back - and it did not, so a
    /// knock was answered into a socket nobody drained and the panel said
    /// "Knocking..." until somebody pressed Stop.
    /// </summary>
    [Fact]
    public void A_knocking_client_reads_the_answer_and_is_in_the_room()
    {
        const int hostPort = LanSessionTests.BasePort + 32;

        var host = new Session("ian", () => DateTime.UtcNow);
        host.Host("ian's room", "seattle_short", "special", 6, secret: "hunter2");

        var guest = new Session("les", () => DateTime.UtcNow);
        Assert.True(guest.Knock($"127.0.0.1:{hostPort}", "hunter2"));

        using var hostWire = LanSession.ForHost(hostPort, () => DateTime.UtcNow);
        using var guestWire = LanSession.ForClient(hostPort, () => DateTime.UtcNow);

        guestWire.SendKnock(
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, hostPort),
            "les", "hunter2");

        LanSessionTests.WaitForDelivery(hostWire);
        hostWire.HostTick(host);

        LanSessionTests.WaitForDelivery(guestWire);
        guestWire.ClientTick(guest, System.Net.IPAddress.Loopback);

        Assert.Equal(SessionPhase.Joined, guest.Phase);
        Assert.Equal(host.Current!.Id, guest.Current!.Id);
    }

    /// <summary>
    /// What a silent knock can still be told about itself. UDP reports no
    /// failure, so these counts are the only difference between "not getting
    /// there" and "getting there and being refused".
    /// </summary>
    [Fact]
    public void A_knock_is_counted_even_when_nothing_answers()
    {
        const int hostPort = LanSessionTests.BasePort + 33;

        using var wire = LanSession.ForClient(hostPort, () => DateTime.UtcNow);

        Assert.Equal(0, wire.KnocksSent);
        Assert.Equal(0, wire.DatagramsHeard);

        // 127.0.0.2 with nothing listening: sent, never answered.
        wire.SendKnock(
            new System.Net.IPEndPoint(System.Net.IPAddress.Parse("127.0.0.2"), hostPort),
            "les", "hunter2");

        Assert.Equal(1, wire.KnocksSent);
        Assert.Equal(0, wire.DatagramsHeard);
    }

    [Fact]
    public void And_what_comes_back_is_counted_too()
    {
        const int hostPort = LanSessionTests.BasePort + 34;

        var host = new Session("ian", () => DateTime.UtcNow);
        host.Host("ian's room", "seattle_short", "special", 6, secret: "hunter2");

        var guest = new Session("les", () => DateTime.UtcNow);
        guest.Knock($"127.0.0.1:{hostPort}", "hunter2");

        using var hostWire = LanSession.ForHost(hostPort, () => DateTime.UtcNow);
        using var guestWire = LanSession.ForClient(hostPort, () => DateTime.UtcNow);

        guestWire.SendKnock(
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, hostPort),
            "les", "hunter2");

        LanSessionTests.WaitForDelivery(hostWire);
        hostWire.HostTick(host);

        LanSessionTests.WaitForDelivery(guestWire);
        guestWire.ClientTick(guest, System.Net.IPAddress.Loopback);

        Assert.Equal(1, guestWire.DatagramsHeard);
        Assert.Equal(hostPort, guestWire.LastHeardFrom!.Port);
    }

    /// <summary>
    /// And how long it has been going, which is what turns a line that never
    /// changes into one that says something is wrong.
    /// </summary>
    [Fact]
    public void A_knock_says_how_long_it_has_been_knocking()
    {
        var now = new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
        var session = new Session("les", () => now);

        Assert.Equal(TimeSpan.Zero, session.KnockingFor);

        session.Knock("192.168.0.9:34719", "hunter2");
        now = now.AddSeconds(12);

        Assert.Equal(12d, session.KnockingFor.TotalSeconds, 1);

        session.Leave();
        Assert.Equal(TimeSpan.Zero, session.KnockingFor);
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
