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

    /// <summary>
    /// Upper bound on datagrams drained in a single <see cref="Tick"/>. Without
    /// this, a flood (or a buggy peer re-broadcasting rapidly) could keep one
    /// Tick draining the socket indefinitely and stall the frame loop. Anything
    /// left over simply waits for the next Tick.
    /// </summary>
    public const int MaxDatagramsPerTick = 32;

    readonly UdpClient _socket;
    readonly int _port;
    readonly Func<DateTime> _clock;
    readonly Dictionary<Guid, (Room Room, DateTime Heard, IPAddress HostAddress)> _seen = [];
    bool _disposed;

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

    /// <summary>
    /// Announcements carrying this id are our own and are ignored. Null (the
    /// default) means "no local room" - distinct from <see cref="Guid.Empty"/>,
    /// which is itself a valid wire value a crafted or buggy datagram could carry.
    /// </summary>
    public Guid? LocalRoomId { get; set; }

    /// <summary>
    /// The exception from the most recent failed <see cref="Announce"/> send, or
    /// null if the last send (if any) succeeded. Occasional loss is expected and
    /// not surfaced as a persistent problem; this exists so a caller can notice
    /// when broadcast is blocked for the whole session and tell the user, rather
    /// than announcing into the void forever with no way to find out.
    /// </summary>
    public SocketException? LastSendFailure { get; private set; }

    public IReadOnlyList<Room> Rooms => _disposed ? [] : [.. _seen.Values.Select(v => v.Room)];

    /// <summary>
    /// The address the most recent announcement for <paramref name="roomId"/>
    /// arrived from - the only place a client can learn where to send its
    /// intent. False for a room that has never been seen, one that has
    /// expired, or after <see cref="Dispose"/>: it is stored alongside the
    /// room in <c>_seen</c>, so it can never outlive it.
    /// </summary>
    public bool TryGetHostAddress(Guid roomId, out IPAddress address)
    {
        if (!_disposed && _seen.TryGetValue(roomId, out var entry))
        {
            address = entry.HostAddress;
            return true;
        }
        address = IPAddress.None;
        return false;
    }

    public void Announce(Room room)
    {
        if (_disposed) return;

        var data = RoomState.Serialise(room);
        try
        {
            _socket.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, _port));
            LastSendFailure = null;
        }
        catch (SocketException ex)
        {
            // A broadcast that cannot go out is not worth interrupting the
            // lobby for; the next announcement is a second away. But remember
            // it, so a persistent failure (as opposed to occasional loss) can
            // eventually be surfaced to the user.
            LastSendFailure = ex;
        }
    }

    public void Tick()
    {
        if (_disposed) return;

        Receive();
        Expire();
    }

    void Receive()
    {
        for (int i = 0; i < MaxDatagramsPerTick && _socket.Available > 0; i++)
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
            if (LocalRoomId is Guid localId && room.Id == localId) continue;
            _seen[room.Id] = (room, _clock(), from!.Address);
        }
    }

    void Expire()
    {
        var now = _clock();
        foreach (var id in _seen.Where(e => now - e.Value.Heard > Timeout)
                                .Select(e => e.Key).ToList())
            _seen.Remove(id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket.Dispose();
    }
}
