using System.Net;
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// The seam the relay needs.
///
/// LanSession used to hold a UdpClient. It uses four things from one - how
/// much is waiting, take the next, send this, and let go - and those four are
/// the whole of this interface. Everything above it (rooms, intents, places,
/// the start barrier) is unchanged and must stay that way: the existing suite
/// is the proof, and these only pin the seam itself.
/// </summary>
public class GameLinkTests
{
    // Clear of every other port this suite binds - see LanSessionTests for why
    // each file needs its own.
    const int BasePort = 34830;

    [Fact]
    public void A_direct_link_binds_the_port_it_was_asked_for()
    {
        using var link = DirectLink.Bind(BasePort);

        Assert.Equal(BasePort, link.BoundPort);
    }

    [Fact]
    public void An_ephemeral_link_gets_a_port_of_its_own()
    {
        using var one = DirectLink.Ephemeral();
        using var two = DirectLink.Ephemeral();

        Assert.True(one.BoundPort > 0);
        Assert.NotEqual(one.BoundPort, two.BoundPort);
    }

    [Fact]
    public void What_goes_in_one_link_comes_out_of_the_other_with_the_sender_named()
    {
        using var listener = DirectLink.Bind(BasePort + 1);
        using var sender = DirectLink.Ephemeral();

        byte[] data = [1, 2, 3, 4];
        sender.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, listener.BoundPort));

        var until = DateTime.UtcNow.AddSeconds(2);
        while (listener.Available == 0 && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.True(listener.Available > 0, "the datagram never arrived");

        IPEndPoint? from = null;
        var got = listener.Receive(ref from);

        Assert.Equal(data, got);
        Assert.NotNull(from);
        Assert.Equal(sender.BoundPort, from!.Port);
    }

    /// <summary>
    /// A session built over a link behaves as one built over a socket - which
    /// is the whole claim of this task.
    /// </summary>
    [Fact]
    public void A_session_can_be_built_over_a_link()
    {
        using var link = DirectLink.Bind(BasePort + 2);
        using var session = LanSession.Over(link, link.BoundPort, () => DateTime.UtcNow,
            hosting: true);

        Assert.Equal(BasePort + 2, session.BoundPort);
    }

    [Fact]
    public void Letting_go_of_a_session_lets_go_of_its_link()
    {
        var link = DirectLink.Bind(BasePort + 3);
        var session = LanSession.Over(link, link.BoundPort, () => DateTime.UtcNow, hosting: true);

        session.Dispose();

        // The port is free again, which it would not be if the link were still
        // holding it.
        using var again = DirectLink.Bind(BasePort + 3);
        Assert.Equal(BasePort + 3, again.BoundPort);
    }
}
