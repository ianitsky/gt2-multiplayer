using System.Net;
using GT2Port.Rendezvous;

namespace GT2Relay;

/// <summary>Something to send, and where.</summary>
public readonly record struct Reply(IPEndPoint To, byte[] Data);

/// <summary>
/// Every decision the relay makes.
///
/// Separated from the socket on purpose. A server's hard parts are expiry,
/// caps and who is allowed to talk to whom, and none of those are easier to
/// think about with a network in the way: here the clock is a function and
/// thirty seconds is a line of test.
///
/// It knows nothing about GT2. A room's description is a blob it was handed
/// and hands back, and relayed traffic is a payload it copies from one
/// datagram into another. That is what lets the game's own protocol change
/// without this being redeployed.
/// </summary>
public sealed class RoomRegistry(Func<DateTime> clock, Random random)
{
    /// <summary>
    /// Caps, because this listens on the open internet. None of them is a
    /// guess about demand - they are the point past which a stranger is
    /// costing somebody else a game.
    /// </summary>
    public const int MaxRooms = 256;
    public const int MaxMembers = 8;

    /// <summary>
    /// How long silence lasts before it means gone. A host publishes every two
    /// seconds and a racing client sends constantly, so thirty seconds is many
    /// missed messages rather than one unlucky one.
    /// </summary>
    public static readonly TimeSpan Forgotten = TimeSpan.FromSeconds(30);

    sealed class Room
    {
        public required Guid Id;
        public required string Code;
        public required IPEndPoint Host;
        public bool Listed;
        public byte[] Card = [];
        public DateTime HeardFromHost;
        public readonly Dictionary<IPEndPoint, DateTime> Members = [];
    }

    readonly Dictionary<Guid, Room> _rooms = [];
    readonly Dictionary<string, Guid> _codes = new(StringComparer.Ordinal);

    public int RoomCount => _rooms.Count;

    public bool TryFindByCode(string code, out Guid roomId) =>
        _codes.TryGetValue(code, out roomId);

    /// <summary>
    /// What to do about one datagram. Returns what to send; an empty list is
    /// the normal answer to anything malformed, unauthorised or unknown -
    /// answering a stranger is how a server becomes something to point at
    /// somebody else.
    /// </summary>
    public IReadOnlyList<Reply> Heard(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!Envelope.TryReadKind(data, out var kind)) return [];

        return kind switch
        {
            Envelope.Kind.Publish => Publish(from, data),
            Envelope.Kind.List => List(from),
            Envelope.Kind.Join => Join(from, data),
            Envelope.Kind.JoinByCode => JoinByCode(from, data),
            Envelope.Kind.Relay => Relay(from, data),
            Envelope.Kind.Leave => Leave(from, data),
            _ => [],
        };
    }

    IReadOnlyList<Reply> Publish(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!Envelope.TryReadPublish(data, out var roomId, out bool listed, out var card))
            return [];

        var now = clock();

        if (!_rooms.TryGetValue(roomId, out var room))
        {
            if (_rooms.Count >= MaxRooms) return [new Reply(from, Envelope.WriteNoRoom())];

            room = new Room { Id = roomId, Code = MintCode(), Host = from };
            _rooms[roomId] = room;
            _codes[room.Code] = roomId;
        }

        // The host may come back on a different port - its NAT hands out a new
        // mapping every time the socket is rebuilt - and when it does, the
        // room moves with it rather than becoming unreachable.
        room.Host = from;
        room.Listed = listed;
        room.Card = card;
        room.HeardFromHost = now;
        room.Members[from] = now;

        return [new Reply(from, Envelope.WritePublished(roomId, room.Code))];
    }

    string MintCode()
    {
        // A collision is one room in a thousand million, and the loop is what
        // makes that number irrelevant rather than something to reason about.
        for (int i = 0; i < 32; i++)
        {
            string code = RoomCode.Next(random);
            if (!_codes.ContainsKey(code)) return code;
        }
        return RoomCode.Next(random);
    }

    IReadOnlyList<Reply> List(IPEndPoint from)
    {
        var replies = new List<Reply>();
        foreach (var room in _rooms.Values)
        {
            if (!room.Listed) continue;
            replies.Add(new Reply(from, Envelope.WriteRoomCard(room.Id, room.Code, room.Card)));
        }
        return replies;
    }

    IReadOnlyList<Reply> Join(IPEndPoint from, ReadOnlySpan<byte> data) =>
        Envelope.TryReadJoin(data, out var roomId)
            ? Admit(from, roomId)
            : [];

    IReadOnlyList<Reply> JoinByCode(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!Envelope.TryReadJoinByCode(data, out string code)) return [];
        if (!_codes.TryGetValue(code, out var roomId))
            return [new Reply(from, Envelope.WriteNoRoom())];
        return Admit(from, roomId);
    }

    IReadOnlyList<Reply> Admit(IPEndPoint from, Guid roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            return [new Reply(from, Envelope.WriteNoRoom())];

        if (!room.Members.ContainsKey(from) && room.Members.Count >= MaxMembers)
            return [new Reply(from, Envelope.WriteNoRoom())];

        room.Members[from] = clock();

        var replies = new List<Reply>
        {
            new(from, Envelope.WriteJoined(roomId)),
            new(from, Envelope.WritePeer(roomId, room.Host)),
        };

        // The host is told too, and this is not a courtesy: the host's own
        // link only sends to endpoints it knows, so without this the answer to
        // the first knock would have nowhere to go.
        if (!from.Equals(room.Host))
            replies.Add(new Reply(room.Host, Envelope.WritePeer(roomId, from)));

        return replies;
    }

    IReadOnlyList<Reply> Relay(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!Envelope.TryReadRelay(data, out var roomId, out var to, out var payload)) return [];
        if (!_rooms.TryGetValue(roomId, out var room)) return [];

        // Both ends have to be in the room. Without the first check this would
        // forward for anybody; without the second it would forward to anybody,
        // and either one is a machine on the internet that sends traffic
        // wherever a stranger points it.
        if (!room.Members.ContainsKey(from)) return [];
        if (!room.Members.ContainsKey(to)) return [];

        room.Members[from] = clock();

        return [new Reply(to, Envelope.WriteRelayed(roomId, from, payload))];
    }

    IReadOnlyList<Reply> Leave(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!Envelope.TryReadLeave(data, out var roomId)) return [];
        if (!_rooms.TryGetValue(roomId, out var room)) return [];

        room.Members.Remove(from);
        return [];
    }

    /// <summary>
    /// Forgets what has gone quiet. Called on a timer by whatever owns this;
    /// nothing expires on its own, so a test can advance a clock and ask.
    /// </summary>
    public void Sweep()
    {
        var now = clock();

        foreach (var roomId in _rooms.Keys.ToList())
        {
            var room = _rooms[roomId];

            if (now - room.HeardFromHost > Forgotten)
            {
                _rooms.Remove(roomId);
                _codes.Remove(room.Code);
                continue;
            }

            foreach (var member in room.Members.Keys.ToList())
            {
                if (member.Equals(room.Host)) continue;
                if (now - room.Members[member] > Forgotten) room.Members.Remove(member);
            }
        }
    }
}
