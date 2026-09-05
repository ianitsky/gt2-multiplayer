using System.Net;
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// A host reachable on this network and through the relay at the same time.
///
/// It used to be one or the other, and whichever list a player had not used
/// was a list of rooms that dropped them three seconds after they joined: a
/// host with a relay configured bound nothing on the well-known port, so
/// everything a player found under "rooms on this network" sent went to a port
/// nobody was listening on.
/// </summary>
public class EitherLinkTests
{
    /// <summary>
    /// A link that records rather than sends, so what reached which path is a
    /// list to assert on instead of a packet capture.
    /// </summary>
    sealed class Fake : IGameLink
    {
        readonly Queue<(byte[] Data, IPEndPoint From)> _waiting = new();

        public List<(byte[] Data, IPEndPoint To)> Sent { get; } = [];
        public bool Disposed { get; private set; }
        public int BoundPort { get; init; }

        public void Deliver(IPEndPoint from, params byte[] data) => _waiting.Enqueue((data, from));

        public int Available => _waiting.Count;

        public byte[] Receive(ref IPEndPoint? from)
        {
            if (_waiting.Count == 0)
                throw new System.Net.Sockets.SocketException(
                    (int)System.Net.Sockets.SocketError.WouldBlock);

            var (data, sender) = _waiting.Dequeue();
            from = sender;
            return data;
        }

        public void Send(byte[] data, int length, IPEndPoint to) =>
            Sent.Add((data[..length], to));

        public IPEndPoint HostAt(IPAddress address, int hostPort) => new(address, hostPort);

        public void Dispose() => Disposed = true;
    }

    static readonly IPEndPoint OnThisNetwork = new(IPAddress.Parse("192.168.15.42"), 51000);
    static readonly IPEndPoint Elsewhere = new(IPAddress.Parse("203.0.113.9"), 41007);

    static (EitherLink Link, Fake Direct, Fake Relayed) Build()
    {
        var direct = new Fake { BoundPort = 34719 };
        var relayed = new Fake { BoundPort = 51234 };
        return (new EitherLink(direct, relayed), direct, relayed);
    }

    [Fact]
    public void Anything_waiting_on_either_path_is_waiting()
    {
        var (link, direct, relayed) = Build();

        Assert.Equal(0, link.Available);

        direct.Deliver(OnThisNetwork, 1);
        relayed.Deliver(Elsewhere, 2);

        Assert.Equal(2, link.Available);
    }

    /// <summary>
    /// Reading has to ask the relay even when the local socket has something,
    /// because asking is what advances a relayed link - it only takes in what
    /// the server said when it is read. A count that stopped early would leave
    /// the relay unread for as long as anybody on this network kept talking.
    /// </summary>
    [Fact]
    public void Asking_how_much_is_waiting_advances_the_relay_too()
    {
        var (link, direct, relayed) = Build();

        direct.Deliver(OnThisNetwork, 1);
        relayed.Deliver(Elsewhere, 2);
        relayed.Deliver(Elsewhere, 3);

        Assert.Equal(3, link.Available);
    }

    [Fact]
    public void Both_paths_are_read()
    {
        var (link, direct, relayed) = Build();

        direct.Deliver(OnThisNetwork, 7);
        relayed.Deliver(Elsewhere, 9);

        IPEndPoint? first = null;
        Assert.Equal([7], link.Receive(ref first));
        Assert.Equal(OnThisNetwork, first);

        IPEndPoint? second = null;
        Assert.Equal([9], link.Receive(ref second));
        Assert.Equal(Elsewhere, second);
    }

    [Fact]
    public void And_an_empty_link_refuses_the_way_a_socket_does()
    {
        var (link, _, _) = Build();

        IPEndPoint? from = null;
        Assert.Throws<System.Net.Sockets.SocketException>(() => link.Receive(ref from));
    }

    /// <summary>
    /// The whole point: an answer goes back the way the question came. A
    /// player on this network is answered on this network, which keeps a local
    /// race off the internet as well as reaching them at all.
    /// </summary>
    [Fact]
    public void A_player_heard_directly_is_answered_directly()
    {
        var (link, direct, relayed) = Build();

        direct.Deliver(OnThisNetwork, 1);
        IPEndPoint? from = null;
        link.Receive(ref from);

        link.Send([5, 6], 2, OnThisNetwork);

        Assert.Equal([5, 6], Assert.Single(direct.Sent).Data);
        Assert.Empty(relayed.Sent);
    }

    [Fact]
    public void And_one_heard_through_the_relay_is_answered_through_it()
    {
        var (link, direct, relayed) = Build();

        relayed.Deliver(Elsewhere, 1);
        IPEndPoint? from = null;
        link.Receive(ref from);

        link.Send([5, 6], 2, Elsewhere);

        Assert.Equal([5, 6], Assert.Single(relayed.Sent).Data);
        Assert.Empty(direct.Sent);
    }

    /// <summary>
    /// Nobody has been heard from yet, so there is nothing to reply to - only
    /// an address, which is what a bare address has always meant.
    /// </summary>
    [Fact]
    public void An_endpoint_nobody_has_spoken_from_is_addressed_directly()
    {
        var (link, direct, relayed) = Build();

        link.Send([1], 1, OnThisNetwork);

        Assert.Single(direct.Sent);
        Assert.Empty(relayed.Sent);
    }

    /// <summary>
    /// A player can change path - a laptop carried onto the host's network, or
    /// a relay that went away. Answering the old way would send to an endpoint
    /// that may no longer be forwarded.
    /// </summary>
    [Fact]
    public void The_latest_path_is_the_one_answered_on()
    {
        var (link, direct, relayed) = Build();
        var same = new IPEndPoint(IPAddress.Parse("198.51.100.4"), 40000);

        relayed.Deliver(same, 1);
        IPEndPoint? first = null;
        link.Receive(ref first);

        direct.Deliver(same, 2);
        IPEndPoint? second = null;
        link.Receive(ref second);

        link.Send([9], 1, same);

        Assert.Single(direct.Sent);
        Assert.Empty(relayed.Sent);
    }

    /// <summary>
    /// The well-known port, because that is the one a player on this network
    /// addresses and the one the room announces. The relay's own port is not
    /// something anybody types.
    /// </summary>
    [Fact]
    public void The_port_it_reports_is_the_one_that_was_bound_here()
    {
        var (link, _, _) = Build();

        Assert.Equal(34719, link.BoundPort);
    }

    /// <summary>
    /// The relay is borrowed. It is also the room list and the keepalive, it
    /// outlives any one session, and closing it would drop the NAT mapping the
    /// whole arrangement rests on.
    /// </summary>
    [Fact]
    public void Disposing_closes_the_socket_it_owns_and_leaves_the_relay_alone()
    {
        var (link, direct, relayed) = Build();

        link.Dispose();

        Assert.True(direct.Disposed);
        Assert.False(relayed.Disposed);
        Assert.Equal(0, link.Available);
    }
}
