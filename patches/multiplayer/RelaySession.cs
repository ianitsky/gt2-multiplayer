using System.Net;
using System.Net.Sockets;
using GT2Port.Rendezvous;

namespace GT2Port.Multiplayer;

/// <summary>
/// A link to a host nobody can address directly, through a server both sides
/// reach outbound.
///
/// This is one socket doing two jobs - asking the server about rooms, and
/// carrying the game's own traffic - and that is deliberate. Two sockets would
/// mean two NAT mappings, each expiring on its own schedule, and the failure
/// that produces is a room that lists fine and then goes quiet, which is a
/// miserable thing to debug.
///
/// It is an <see cref="IGameLink"/>, so <see cref="LanSession"/> and
/// everything above it works unchanged: <see cref="Receive"/> names the peer
/// that sent the payload rather than the relay that carried it, which is the
/// identity the lobby keys players by.
///
/// Nothing here understands the game's protocol either. A room's card is
/// whatever <see cref="RoomState.Serialise"/> produced and a payload is
/// whatever <see cref="LanSession"/> sent; both are bytes to be carried.
/// </summary>
public sealed class RelaySession : IGameLink
{
    /// <summary>
    /// How often a host says it is still there. Also the keepalive that holds
    /// the NAT mapping open - two seconds is well inside the shortest UDP
    /// timeout a home router is likely to use.
    /// </summary>
    public static readonly TimeSpan PublishEvery = TimeSpan.FromSeconds(2);

    /// <summary>How often a browser asks what rooms there are.</summary>
    public static readonly TimeSpan ListEvery = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a room stays on the list after it was last heard of. Longer
    /// than <see cref="ListEvery"/> by enough that one lost datagram does not
    /// make a room flicker out and back.
    /// </summary>
    public static readonly TimeSpan Forgotten = TimeSpan.FromSeconds(7);

    /// <summary>A room the server told us about.</summary>
    public readonly record struct Advert(Guid Id, string Code, byte[] Card);

    readonly UdpClient _socket;
    readonly IPEndPoint _relay;
    readonly Func<DateTime> _clock;

    readonly Queue<(byte[] Payload, IPEndPoint From)> _waiting = [];
    readonly Dictionary<Guid, (Advert Advert, DateTime Heard)> _rooms = [];
    readonly List<IPEndPoint> _peers = [];

    DateTime? _lastPublish;
    DateTime? _lastList;
    bool _disposed;

    public RelaySession(IPEndPoint relay, Func<DateTime> clock)
    {
        _relay = relay;
        _clock = clock;

        _socket = new UdpClient { Client = { ReceiveTimeout = 1 } };
        try
        {
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }

        // A relay that is not listening makes Windows deliver an ICMP refusal
        // as a ConnectionReset on the *next* receive, which would look like
        // the link breaking rather than like nobody answering.
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            _socket.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
        }

        BoundPort = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
    }

    /// <summary>
    /// The local port. Not what peers address - that is the public endpoint
    /// the server saw, which arrives as a <see cref="Peers"/> entry on the
    /// other side.
    /// </summary>
    public int BoundPort { get; }

    public Guid RoomId { get; private set; }
    public string Code { get; private set; } = "";
    public bool Admitted { get; private set; }
    public bool Refused { get; private set; }

    /// <summary>How many publishes have actually gone out, for a test to count.</summary>
    public int Published { get; private set; }

    public IReadOnlyList<IPEndPoint> Peers => _peers;

    /// <summary>
    /// The rooms the server has mentioned lately. Kept with the time each was
    /// heard, so one that stops being mentioned falls off rather than sitting
    /// there being unjoinable.
    /// </summary>
    public IReadOnlyList<Advert> Rooms
    {
        get
        {
            var now = _clock();
            return [.. _rooms.Values.Where(r => now - r.Heard <= Forgotten).Select(r => r.Advert)];
        }
    }

    public void Publish(Guid roomId, bool listed, byte[] card)
    {
        if (_disposed) return;

        var now = _clock();
        if (_lastPublish is { } last && now - last < PublishEvery) return;
        _lastPublish = now;

        RoomId = roomId;
        Published++;
        Tell(Envelope.WritePublish(roomId, listed, card));
    }

    public void AskForRooms()
    {
        if (_disposed) return;

        var now = _clock();
        if (_lastList is { } last && now - last < ListEvery) return;
        _lastList = now;

        Tell(Envelope.WriteList());
    }

    public void Join(Guid roomId)
    {
        Refused = false;
        Tell(Envelope.WriteJoin(roomId));
    }

    public void JoinByCode(string code)
    {
        Refused = false;
        string tidy = RoomCode.Tidy(code);
        if (!RoomCode.IsWellFormed(tidy))
        {
            // Refused here rather than sent, so a typo is answered by this
            // machine instead of costing a round trip to say the same thing.
            Refused = true;
            return;
        }
        Tell(Envelope.WriteJoinByCode(tidy));
    }

    public void Leave()
    {
        if (RoomId == Guid.Empty) return;
        Tell(Envelope.WriteLeave(RoomId));
        Admitted = false;
    }

    void Tell(byte[] data)
    {
        if (_disposed) return;
        try { _socket.Send(data, data.Length, _relay); }
        catch (SocketException) { /* the next tick tries again */ }
    }

    // ---- IGameLink ----

    /// <summary>
    /// Drains the socket first, because everything that arrives here is
    /// wrapped and most of it is not game traffic at all - a count of what the
    /// socket holds would be a count of envelopes, not of payloads.
    /// </summary>
    public int Available
    {
        get
        {
            Pump();
            return _waiting.Count;
        }
    }

    public byte[] Receive(ref IPEndPoint? from)
    {
        Pump();
        if (_waiting.Count == 0) throw new SocketException((int)SocketError.WouldBlock);

        var (payload, sender) = _waiting.Dequeue();
        from = sender;
        return payload;
    }

    public void Send(byte[] data, int length, IPEndPoint to)
    {
        if (_disposed) return;
        if (length > Envelope.MaxPayload) return;

        var wrapped = Envelope.WriteRelay(RoomId, to, data.AsSpan(0, length));
        Tell(wrapped);
    }

    void Pump()
    {
        if (_disposed) return;

        while (_socket.Available > 0)
        {
            IPEndPoint? from = null;
            byte[] data;
            try { data = _socket.Receive(ref from); }
            catch (SocketException) { return; }

            // Only the relay is listened to. Anything else reaching this port
            // is a scanner or a stray, and treating it as game traffic would
            // put a stranger's bytes into the lobby.
            if (from is null || !from.Equals(_relay)) continue;
            if (!Envelope.TryReadKind(data, out var kind)) continue;

            switch (kind)
            {
                case Envelope.Kind.Published:
                    if (Envelope.TryReadPublished(data, out var published, out string code))
                    {
                        RoomId = published;
                        Code = code;
                    }
                    break;

                case Envelope.Kind.RoomCard:
                    if (Envelope.TryReadRoomCard(data, out var id, out string roomCode, out var card))
                        _rooms[id] = (new Advert(id, roomCode, card), _clock());
                    break;

                case Envelope.Kind.Joined:
                    if (Envelope.TryReadJoined(data, out var joined))
                    {
                        RoomId = joined;
                        Admitted = true;
                        Refused = false;
                    }
                    break;

                case Envelope.Kind.Peer:
                    if (Envelope.TryReadPeer(data, out _, out var peer) && !_peers.Contains(peer))
                        _peers.Add(peer);
                    break;

                case Envelope.Kind.Relayed:
                    if (Envelope.TryReadRelayed(data, out _, out var sender, out var payload))
                    {
                        // A peer that spoke is a peer worth knowing about, even
                        // if the server's introduction went missing.
                        if (!_peers.Contains(sender)) _peers.Add(sender);
                        _waiting.Enqueue((payload, sender));
                    }
                    break;

                case Envelope.Kind.NoRoom:
                    Refused = true;
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Leave();
        _disposed = true;
        _socket.Dispose();
    }
}
