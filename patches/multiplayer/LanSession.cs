using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GT2Port.Multiplayer;

/// <summary>
/// Carries room membership between a joined client and the host it joined.
///
/// Discovery is host-to-broadcast; this is the other half. A client repeatedly
/// sends its whole intent (name, car, ready, leaving) rather than individual
/// events, for the same reason the host retransmits room state whole: a lost
/// packet costs nothing because the next one supersedes it. There are no acks,
/// no sequence numbers, no ordering to get wrong.
///
/// The host never targets an address table - it replies to whoever a client
/// message just arrived from. That is what keeps this channel free of any
/// separate broadcast timer or membership bookkeeping of its own; Session
/// already tracks who is in the room.
/// </summary>
public sealed class LanSession : IDisposable
{
    /// <summary>
    /// Upper bound on datagrams drained in a single tick. See
    /// <see cref="LanDiscovery.MaxDatagramsPerTick"/> for why this exists.
    /// </summary>
    public const int MaxDatagramsPerTick = 32;

    /// <summary>
    /// The client sends its intent no more than this often. Five times a
    /// second, expressed as the minimum spacing between sends rather than a
    /// count, and measured from the injected clock rather than a frame
    /// counter or Stopwatch - the lobby loop's own sleep drifts, and tests
    /// need to be able to drive the rate deterministically.
    /// </summary>
    static readonly TimeSpan IntentInterval = TimeSpan.FromSeconds(1.0 / 5.0);

    static readonly byte[] Magic = "G2CS"u8.ToArray();
    const byte Version = 1;
    const int MaxStringBytes = RoomState.MaxStringBytes;

    const byte ReadyFlag = 1 << 0;
    const byte LeavingFlag = 1 << 1;

    readonly UdpClient _socket;
    readonly int _port;
    readonly Func<DateTime> _clock;
    DateTime? _lastIntentSent;
    bool _disposed;

    public LanSession(int port, Func<DateTime> clock)
    {
        _port = port;
        _clock = clock;
        _socket = new UdpClient
        {
            Client = { ReceiveTimeout = 1 },
        };
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    /// <summary>
    /// The exception from the most recent failed send, or null if the last
    /// send (if any) succeeded. See <see cref="LanDiscovery.LastSendFailure"/>
    /// for why this is remembered rather than thrown.
    /// </summary>
    public SocketException? LastSendFailure { get; private set; }

    /// <summary>
    /// Drains the socket. For every well-formed client message whose room id
    /// matches <c>session.Current.Id</c>, applies it to the session and
    /// replies to that sender with the room state as it stands after
    /// applying. A message for any other room id - including a well-formed
    /// one - is ignored outright: not applied, not replied to.
    /// </summary>
    public void HostTick(Session session)
    {
        if (_disposed) return;
        if (session.Phase != SessionPhase.Hosting) return;
        if (session.Current is not { } room) return;

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

            if (!TryDeserialise(data, out var intent)) continue;
            if (intent.RoomId != room.Id) continue;

            if (intent.Leaving)
                session.ApplyClientLeave(intent.Name);
            else
                session.ApplyClientIntent(intent.Name, intent.Car, intent.Ready);

            SendRoomState(session.Current!, from!);
        }
    }

    /// <summary>
    /// Drains the socket, feeding each well-formed room announcement to
    /// <see cref="Session.OnRemoteState"/>, then - no more than
    /// <see cref="IntentInterval"/> often - sends this player's own intent to
    /// <paramref name="hostAddress"/>.
    /// </summary>
    public void ClientTick(Session session, IPAddress hostAddress)
    {
        if (_disposed) return;
        if (session.Phase != SessionPhase.Joined) return;

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
            session.OnRemoteState(room);
        }

        if (session.Current is not { } current) return;

        var now = _clock();
        if (_lastIntentSent is { } last && now - last < IntentInterval) return;

        var self = current.Players.FirstOrDefault(p => p.Name == session.PlayerName);
        var intent = new ClientIntent(current.Id, session.PlayerName,
            self?.Car ?? "", self?.Ready ?? false, Leaving: false);
        SendIntent(intent, hostAddress);
        _lastIntentSent = now;
    }

    /// <summary>
    /// Sends one message with the leaving flag set. Best-effort - not
    /// retried - because the host's own timeout is what guarantees the
    /// player eventually disappears even if this is lost.
    /// </summary>
    public void SendLeave(Session session, IPAddress hostAddress)
    {
        if (_disposed) return;
        if (session.Current is not { } current) return;

        var self = current.Players.FirstOrDefault(p => p.Name == session.PlayerName);
        var intent = new ClientIntent(current.Id, session.PlayerName,
            self?.Car ?? "", self?.Ready ?? false, Leaving: true);
        SendIntent(intent, hostAddress);
    }

    void SendIntent(ClientIntent intent, IPAddress hostAddress) =>
        Send(Serialise(intent), new IPEndPoint(hostAddress, _port));

    void SendRoomState(Room room, IPEndPoint to) =>
        Send(RoomState.Serialise(room), to);

    void Send(byte[] data, IPEndPoint to)
    {
        try
        {
            _socket.Send(data, data.Length, to);
            LastSendFailure = null;
        }
        catch (SocketException ex)
        {
            LastSendFailure = ex;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket.Dispose();
    }

    // ---- wire format ----

    internal readonly record struct ClientIntent(Guid RoomId, string Name, string Car, bool Ready, bool Leaving);

    internal static byte[] Serialise(ClientIntent intent)
    {
        var buffer = new List<byte>();
        buffer.AddRange(Magic);
        buffer.Add(Version);
        buffer.AddRange(intent.RoomId.ToByteArray());

        byte flags = 0;
        if (intent.Ready) flags |= ReadyFlag;
        if (intent.Leaving) flags |= LeavingFlag;
        buffer.Add(flags);

        WriteString(buffer, intent.Name);
        WriteString(buffer, intent.Car);
        return [.. buffer];
    }

    internal static bool TryDeserialise(byte[] data, out ClientIntent intent)
    {
        intent = default;
        ReadOnlySpan<byte> span = data;
        int offset = 0;

        if (span.Length < Magic.Length || !span[..Magic.Length].SequenceEqual(Magic)) return false;
        offset += Magic.Length;

        if (!TryByte(span, ref offset, out byte version) || version != Version) return false;
        if (span.Length - offset < 16) return false;
        var roomId = new Guid(span.Slice(offset, 16));
        offset += 16;

        if (!TryByte(span, ref offset, out byte flags)) return false;
        if (!TryString(span, ref offset, out string name)) return false;
        if (!TryString(span, ref offset, out string car)) return false;

        intent = new ClientIntent(roomId, name, car,
            Ready: (flags & ReadyFlag) != 0, Leaving: (flags & LeavingFlag) != 0);
        return true;
    }

    static void WriteString(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(TruncateToUtf8ByteLimit(value, MaxStringBytes));
        buffer.Add((byte)bytes.Length);
        buffer.AddRange(bytes);
    }

    /// <summary>
    /// Truncates to at most <paramref name="maxBytes"/> UTF-8 bytes on a
    /// codepoint boundary. Same approach as <c>RoomState</c>'s - see there
    /// for the rationale - reimplemented here because that one is private to
    /// <c>RoomState</c>.
    /// </summary>
    static string TruncateToUtf8ByteLimit(string value, int maxBytes)
    {
        int byteCount = 0;
        int charsToKeep = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            int runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > maxBytes) break;
            byteCount += runeBytes;
            charsToKeep += rune.Utf16SequenceLength;
        }
        return charsToKeep == value.Length ? value : value[..charsToKeep];
    }

    static bool TryByte(ReadOnlySpan<byte> data, ref int offset, out byte value)
    {
        if (offset >= data.Length) { value = 0; return false; }
        value = data[offset++];
        return true;
    }

    static bool TryString(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = "";
        if (!TryByte(data, ref offset, out byte length)) return false;
        if (length > MaxStringBytes || data.Length - offset < length) return false;
        value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return true;
    }
}
