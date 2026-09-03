using System.Net;

namespace GT2Port.Multiplayer;

/// <summary>
/// Where a session's datagrams go, and where they come from.
///
/// This is the four things <see cref="LanSession"/> ever asked a UdpClient
/// for, and no more. It exists because the relay needed a second answer to
/// "how do these bytes reach that player" - over the local network they are
/// simply addressed to it, and over the internet they are wrapped up and sent
/// to a server that passes them on. Everything above this seam - rooms,
/// intents, places, the start barrier - cannot tell the difference, which is
/// the point: none of it should have to learn about NAT.
///
/// <see cref="Receive"/> hands back who sent it, and that identity is what the
/// lobby keys players by. A relayed link therefore reports the *peer's* public
/// address rather than the relay's, or every player in a room would look like
/// the same one.
/// </summary>
public interface IGameLink : IDisposable
{
    /// <summary>How many datagrams are waiting.</summary>
    int Available { get; }

    /// <summary>
    /// Takes the next one. Throws <see cref="System.Net.Sockets.SocketException"/>
    /// the way a UdpClient does, because every caller already handles that.
    /// </summary>
    byte[] Receive(ref IPEndPoint? from);

    void Send(byte[] data, int length, IPEndPoint to);

    /// <summary>The port this machine is reachable on, as this machine sees it.</summary>
    int BoundPort { get; }

    /// <summary>
    /// Where to send something to the host, given the address it was found at
    /// and the well-known port a host answers on.
    ///
    /// On a local network that is simply the two put together, which is what
    /// every caller used to build for itself. Through a relay it is not: the
    /// host is reached at the public endpoint the server saw, whose port is
    /// whatever its NAT handed out, and a datagram addressed to the well-known
    /// port names nobody the relay will forward to. That is an addressing
    /// question, so it belongs to the thing that does the addressing.
    /// </summary>
    IPEndPoint HostAt(IPAddress address, int hostPort);
}
