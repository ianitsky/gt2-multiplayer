using System.Net;
using System.Net.Sockets;

namespace GT2Port.Multiplayer;

/// <summary>
/// A link that is just a socket - what every session was before the relay
/// existed, and still what one is on a local network.
///
/// The binding rules are the ones <see cref="LanSession"/> had and the reasons
/// have not changed: a host takes the well-known port without
/// <see cref="SocketOptionName.ReuseAddress"/>, so a second host on the same
/// machine fails loudly instead of binding successfully and then silently
/// receiving nothing; a client takes port 0 so two clients on one machine
/// never collide.
/// </summary>
public sealed class DirectLink : IGameLink
{
    readonly UdpClient _socket;
    bool _disposed;

    DirectLink(UdpClient socket)
    {
        _socket = socket;
        BoundPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    public int BoundPort { get; }

    public static DirectLink Bind(int port) => Open(port);

    public static DirectLink Ephemeral() => Open(0);

    static DirectLink Open(int port)
    {
        var socket = new UdpClient
        {
            Client = { ReceiveTimeout = 1 },
        };
        try
        {
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch
        {
            // Leaving it undisposed leaks a handle per failed attempt - a
            // player clicking "Create a room" while another instance already
            // holds the port does exactly that, repeatedly.
            socket.Dispose();
            throw;
        }
        return new DirectLink(socket);
    }

    public IPEndPoint HostAt(IPAddress address, int hostPort) => new(address, hostPort);

    public int Available => _disposed ? 0 : _socket.Available;

    public byte[] Receive(ref IPEndPoint? from) => _socket.Receive(ref from);

    public void Send(byte[] data, int length, IPEndPoint to) => _socket.Send(data, length, to);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket.Dispose();
    }
}
