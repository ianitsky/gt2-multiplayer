using System.Net;
using System.Net.Sockets;

namespace GT2Port.Multiplayer;

/// <summary>
/// Finds rooms on the local network, and announces one.
///
/// UDP broadcast, so there is no server to run and nothing to configure. The
/// same socket carries the netcode later, which is why this is not TCP.
///
/// A room is remembered only while it keeps announcing: a host that quits or
/// unplugs simply stops, and its room ages out. Nothing has to be told.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    readonly UdpClient _socket;
    readonly int _port;
    readonly Func<DateTime> _clock;
    readonly Dictionary<Guid, (Room Room, DateTime Heard)> _seen = [];

    public LanDiscovery(int port, Func<DateTime> clock)
    {
        _port = port;
        _clock = clock;
        _socket = new UdpClient
        {
            EnableBroadcast = true,
            Client = { ReceiveTimeout = 1 },
        };
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    /// <summary>Announcements carrying this id are our own and are ignored.</summary>
    public Guid LocalRoomId { get; set; }

    public IReadOnlyList<Room> Rooms => [.. _seen.Values.Select(v => v.Room)];

    public void Announce(Room room)
    {
        var data = RoomState.Serialise(room);
        try
        {
            _socket.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, _port));
        }
        catch (SocketException)
        {
            // A broadcast that cannot go out is not worth interrupting the
            // lobby for; the next announcement is a second away.
        }
    }

    public void Tick()
    {
        Receive();
        Expire();
    }

    void Receive()
    {
        while (_socket.Available > 0)
        {
            IPEndPoint? from = null;
            byte[] data;
            try
            {
                data = _socket.Receive(ref from);
            }
            catch (SocketException)
            {
                return;
            }

            if (!RoomState.TryDeserialise(data, out var room)) continue;
            if (room.Id == LocalRoomId) continue;
            _seen[room.Id] = (room, _clock());
        }
    }

    void Expire()
    {
        var now = _clock();
        foreach (var id in _seen.Where(e => now - e.Value.Heard > Timeout)
                                .Select(e => e.Key).ToList())
            _seen.Remove(id);
    }

    public void Dispose() => _socket.Dispose();
}
