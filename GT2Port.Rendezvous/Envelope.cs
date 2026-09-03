using System.Buffers.Binary;
using System.Net;

namespace GT2Port.Rendezvous;

/// <summary>
/// What a player and the rendezvous server say to each other.
///
/// Deliberately not the game's own format. The server stores a room's
/// description and forwards a room's traffic without looking inside either:
/// the description is whatever <c>RoomState.Serialise</c> produced and the
/// traffic is whatever <c>LanSession</c> sent, and both are just a length and
/// some bytes here. That is what lets the game's protocol change - a new field
/// in a room, a new kind of message during a race - without anybody having to
/// redeploy a server, and it is why this project has no reference to the game
/// at all.
///
/// Every read is a <c>TryRead</c> and never throws. These bytes arrive from
/// the open internet, where a length field that promises more than the
/// datagram carries is not an edge case but the first thing anybody tries.
/// Writes do throw, because a message this program built too large is a bug in
/// this program.
/// </summary>
public static class Envelope
{
    /// <summary>
    /// Distinct from LanSession's 0xA5 on purpose. The two protocols share a
    /// socket in <c>RelaySession</c>, so a byte that told them apart only by
    /// context would be a byte that eventually gets it wrong.
    /// </summary>
    public const byte Magic = 0xA6;

    public const byte Version = 1;

    /// <summary>Magic, version, kind.</summary>
    public const int HeaderBytes = 3;

    public const int MaxCard = 1024;
    public const int MaxPayload = 1024;

    /// <summary>
    /// What a datagram may total. Comfortably inside the smallest path MTU
    /// anybody still meets, so nothing this sends is ever fragmented - a
    /// fragmented UDP datagram is one that a middlebox somewhere will drop.
    /// </summary>
    public const int MaxDatagram = 1200;

    public const int CodeLength = RoomCode.Length;

    public enum Kind : byte
    {
        Publish = 1,
        Published = 2,
        List = 3,
        RoomCard = 4,
        Join = 5,
        JoinByCode = 6,
        Joined = 7,
        Peer = 8,
        Relay = 9,
        Relayed = 10,
        NoRoom = 11,
        Leave = 12,
    }

    public static bool TryReadKind(ReadOnlySpan<byte> data, out Kind kind)
    {
        kind = default;
        if (data.Length < HeaderBytes) return false;
        if (data[0] != Magic || data[1] != Version) return false;
        if (!Enum.IsDefined((Kind)data[2])) return false;
        kind = (Kind)data[2];
        return true;
    }

    static bool Is(ReadOnlySpan<byte> data, Kind kind) =>
        TryReadKind(data, out var got) && got == kind;

    static byte[] Head(Kind kind, int extra)
    {
        var bytes = new byte[HeaderBytes + extra];
        bytes[0] = Magic;
        bytes[1] = Version;
        bytes[2] = (byte)kind;
        return bytes;
    }

    // ---- room ids ----

    static void WriteGuid(Span<byte> to, Guid id) => id.TryWriteBytes(to);

    static bool TryReadGuid(ReadOnlySpan<byte> data, int at, out Guid id)
    {
        id = Guid.Empty;
        if (data.Length < at + 16) return false;
        id = new Guid(data.Slice(at, 16));
        return true;
    }

    // ---- endpoints ----
    //
    // Four bytes and a port: IPv4 only. An IPv6 peer reaching an IPv4 relay
    // has no NAT to punch through in the first place, and mixing the two here
    // would mean a variable-length field in the middle of every relayed
    // datagram for a case this stage does not serve.

    const int EndPointBytes = 6;

    static void WriteEndPoint(Span<byte> to, IPEndPoint where)
    {
        Span<byte> address = stackalloc byte[4];
        if (!where.Address.TryWriteBytes(address, out int written) || written != 4)
            throw new ArgumentException("only IPv4 endpoints travel in an envelope", nameof(where));
        address.CopyTo(to);
        BinaryPrimitives.WriteUInt16LittleEndian(to[4..], (ushort)where.Port);
    }

    static bool TryReadEndPoint(ReadOnlySpan<byte> data, int at, out IPEndPoint where)
    {
        where = new IPEndPoint(IPAddress.Any, 0);
        if (data.Length < at + EndPointBytes) return false;
        var address = new IPAddress(data.Slice(at, 4));
        ushort port = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at + 4, 2));
        where = new IPEndPoint(address, port);
        return true;
    }

    // ---- codes ----

    static void WriteCode(Span<byte> to, string code)
    {
        if (!RoomCode.IsWellFormed(code))
            throw new ArgumentException($"\"{code}\" is not a room code", nameof(code));
        for (int i = 0; i < CodeLength; i++) to[i] = (byte)code[i];
    }

    static bool TryReadCode(ReadOnlySpan<byte> data, int at, out string code)
    {
        code = "";
        if (data.Length < at + CodeLength) return false;

        Span<char> chars = stackalloc char[CodeLength];
        for (int i = 0; i < CodeLength; i++) chars[i] = (char)data[at + i];

        string read = new(chars);
        if (!RoomCode.IsWellFormed(read)) return false;

        code = read;
        return true;
    }

    // ---- blobs ----

    static void WriteBlob(Span<byte> to, ReadOnlySpan<byte> blob)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(to, (ushort)blob.Length);
        blob.CopyTo(to[2..]);
    }

    static bool TryReadBlob(ReadOnlySpan<byte> data, int at, int most, out byte[] blob)
    {
        blob = [];
        if (data.Length < at + 2) return false;

        int length = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at, 2));
        if (length > most) return false;
        if (data.Length < at + 2 + length) return false;

        blob = data.Slice(at + 2, length).ToArray();
        return true;
    }

    // ---- Publish / Published ----

    public static byte[] WritePublish(Guid roomId, bool listed, ReadOnlySpan<byte> card)
    {
        if (card.Length > MaxCard)
            throw new ArgumentException($"a card is at most {MaxCard} bytes", nameof(card));

        var bytes = Head(Kind.Publish, 16 + 1 + 2 + card.Length);
        WriteGuid(bytes.AsSpan(HeaderBytes), roomId);
        bytes[HeaderBytes + 16] = (byte)(listed ? 1 : 0);
        WriteBlob(bytes.AsSpan(HeaderBytes + 17), card);
        return bytes;
    }

    public static bool TryReadPublish(
        ReadOnlySpan<byte> data, out Guid roomId, out bool listed, out byte[] card)
    {
        roomId = Guid.Empty;
        listed = false;
        card = [];

        if (!Is(data, Kind.Publish)) return false;
        if (!TryReadGuid(data, HeaderBytes, out roomId)) return false;
        if (data.Length < HeaderBytes + 17) return false;

        listed = data[HeaderBytes + 16] != 0;
        return TryReadBlob(data, HeaderBytes + 17, MaxCard, out card);
    }

    public static byte[] WritePublished(Guid roomId, string code)
    {
        var bytes = Head(Kind.Published, 16 + CodeLength);
        WriteGuid(bytes.AsSpan(HeaderBytes), roomId);
        WriteCode(bytes.AsSpan(HeaderBytes + 16), code);
        return bytes;
    }

    public static bool TryReadPublished(ReadOnlySpan<byte> data, out Guid roomId, out string code)
    {
        roomId = Guid.Empty;
        code = "";
        if (!Is(data, Kind.Published)) return false;
        if (!TryReadGuid(data, HeaderBytes, out roomId)) return false;
        return TryReadCode(data, HeaderBytes + 16, out code);
    }

    // ---- List / RoomCard ----

    public static byte[] WriteList() => Head(Kind.List, 0);

    /// <summary>
    /// One room, one datagram. A listing that packed every room into one
    /// message would be a message whose size depends on how popular the server
    /// is that evening, and the first busy night would be the night it grew
    /// past what a datagram carries.
    /// </summary>
    public static byte[] WriteRoomCard(Guid roomId, string code, ReadOnlySpan<byte> card)
    {
        if (card.Length > MaxCard)
            throw new ArgumentException($"a card is at most {MaxCard} bytes", nameof(card));

        var bytes = Head(Kind.RoomCard, 16 + CodeLength + 2 + card.Length);
        WriteGuid(bytes.AsSpan(HeaderBytes), roomId);
        WriteCode(bytes.AsSpan(HeaderBytes + 16), code);
        WriteBlob(bytes.AsSpan(HeaderBytes + 16 + CodeLength), card);
        return bytes;
    }

    public static bool TryReadRoomCard(
        ReadOnlySpan<byte> data, out Guid roomId, out string code, out byte[] card)
    {
        roomId = Guid.Empty;
        code = "";
        card = [];

        if (!Is(data, Kind.RoomCard)) return false;
        if (!TryReadGuid(data, HeaderBytes, out roomId)) return false;
        if (!TryReadCode(data, HeaderBytes + 16, out code)) return false;
        return TryReadBlob(data, HeaderBytes + 16 + CodeLength, MaxCard, out card);
    }

    // ---- Join / JoinByCode / Joined / Leave ----

    static byte[] WriteRoomOnly(Kind kind, Guid roomId)
    {
        var bytes = Head(kind, 16);
        WriteGuid(bytes.AsSpan(HeaderBytes), roomId);
        return bytes;
    }

    static bool TryReadRoomOnly(ReadOnlySpan<byte> data, Kind kind, out Guid roomId)
    {
        roomId = Guid.Empty;
        if (!Is(data, kind)) return false;
        return TryReadGuid(data, HeaderBytes, out roomId);
    }

    public static byte[] WriteJoin(Guid roomId) => WriteRoomOnly(Kind.Join, roomId);

    public static bool TryReadJoin(ReadOnlySpan<byte> data, out Guid roomId) =>
        TryReadRoomOnly(data, Kind.Join, out roomId);

    public static byte[] WriteJoined(Guid roomId) => WriteRoomOnly(Kind.Joined, roomId);

    public static bool TryReadJoined(ReadOnlySpan<byte> data, out Guid roomId) =>
        TryReadRoomOnly(data, Kind.Joined, out roomId);

    public static byte[] WriteLeave(Guid roomId) => WriteRoomOnly(Kind.Leave, roomId);

    public static bool TryReadLeave(ReadOnlySpan<byte> data, out Guid roomId) =>
        TryReadRoomOnly(data, Kind.Leave, out roomId);

    public static byte[] WriteJoinByCode(string code)
    {
        var bytes = Head(Kind.JoinByCode, CodeLength);
        WriteCode(bytes.AsSpan(HeaderBytes), code);
        return bytes;
    }

    public static bool TryReadJoinByCode(ReadOnlySpan<byte> data, out string code)
    {
        code = "";
        if (!Is(data, Kind.JoinByCode)) return false;
        return TryReadCode(data, HeaderBytes, out code);
    }

    public static byte[] WriteNoRoom() => Head(Kind.NoRoom, 0);

    // ---- Peer ----

    public static byte[] WritePeer(Guid roomId, IPEndPoint peer)
    {
        var bytes = Head(Kind.Peer, 16 + EndPointBytes);
        WriteGuid(bytes.AsSpan(HeaderBytes), roomId);
        WriteEndPoint(bytes.AsSpan(HeaderBytes + 16), peer);
        return bytes;
    }

    public static bool TryReadPeer(ReadOnlySpan<byte> data, out Guid roomId, out IPEndPoint peer)
    {
        roomId = Guid.Empty;
        peer = new IPEndPoint(IPAddress.Any, 0);
        if (!Is(data, Kind.Peer)) return false;
        if (!TryReadGuid(data, HeaderBytes, out roomId)) return false;
        return TryReadEndPoint(data, HeaderBytes + 16, out peer);
    }

    // ---- Relay / Relayed ----

    static byte[] WriteCarried(Kind kind, Guid roomId, IPEndPoint who, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
            throw new ArgumentException($"a payload is at most {MaxPayload} bytes", nameof(payload));

        var bytes = Head(kind, 16 + EndPointBytes + 2 + payload.Length);
        WriteGuid(bytes.AsSpan(HeaderBytes), roomId);
        WriteEndPoint(bytes.AsSpan(HeaderBytes + 16), who);
        WriteBlob(bytes.AsSpan(HeaderBytes + 16 + EndPointBytes), payload);
        return bytes;
    }

    static bool TryReadCarried(
        ReadOnlySpan<byte> data, Kind kind,
        out Guid roomId, out IPEndPoint who, out byte[] payload)
    {
        roomId = Guid.Empty;
        who = new IPEndPoint(IPAddress.Any, 0);
        payload = [];

        if (!Is(data, kind)) return false;
        if (!TryReadGuid(data, HeaderBytes, out roomId)) return false;
        if (!TryReadEndPoint(data, HeaderBytes + 16, out who)) return false;
        return TryReadBlob(data, HeaderBytes + 16 + EndPointBytes, MaxPayload, out payload);
    }

    public static byte[] WriteRelay(Guid roomId, IPEndPoint to, ReadOnlySpan<byte> payload) =>
        WriteCarried(Kind.Relay, roomId, to, payload);

    public static bool TryReadRelay(
        ReadOnlySpan<byte> data, out Guid roomId, out IPEndPoint to, out byte[] payload) =>
        TryReadCarried(data, Kind.Relay, out roomId, out to, out payload);

    public static byte[] WriteRelayed(Guid roomId, IPEndPoint from, ReadOnlySpan<byte> payload) =>
        WriteCarried(Kind.Relayed, roomId, from, payload);

    public static bool TryReadRelayed(
        ReadOnlySpan<byte> data, out Guid roomId, out IPEndPoint from, out byte[] payload) =>
        TryReadCarried(data, Kind.Relayed, out roomId, out from, out payload);
}
