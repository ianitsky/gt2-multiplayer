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
    /// <summary>
    /// Two, since a client's intent gained the paint it chose. A host running
    /// the older format rejects this outright rather than reading the colour
    /// byte as the start of something else.
    /// </summary>
    const byte Version = 2;
    const int MaxStringBytes = RoomState.MaxStringBytes;

    const byte ReadyFlag = 1 << 0;
    const byte LeavingFlag = 1 << 1;

    /// <summary>
    /// This player is here to watch. A bit rather than a byte of its own, so
    /// the intent's layout does not move and its version need not: a host that
    /// does not know the bit ignores it and sees a driver, which is what it
    /// would have seen anyway.
    /// </summary>
    const byte WatchingFlag = 1 << 2;

    readonly UdpClient _socket;
    readonly int _boundPort;
    readonly int _hostPort;

    /// <summary>
    /// Whether this socket is the host's. Until the places had to be passed on,
    /// only the caller needed to know which role it had built - the two factory
    /// methods differ in how they bind and in nothing else. The relay is the
    /// first thing the socket itself has to decide.
    /// </summary>
    readonly bool _hosting;
    readonly Func<DateTime> _clock;
    DateTime? _lastIntentSent;
    bool _disposed;

    LanSession(UdpClient socket, int boundPort, int hostPort, Func<DateTime> clock, bool hosting)
    {
        _socket = socket;
        _boundPort = boundPort;
        _hostPort = hostPort;
        _clock = clock;
        _hosting = hosting;
    }

    /// <summary>
    /// Binds <paramref name="port"/> - the well-known port clients address -
    /// without <see cref="SocketOptionName.ReuseAddress"/>. A second host on
    /// this machine binding the identical port would otherwise succeed and
    /// then silently receive nothing, which is exactly the failure this
    /// class exists to remove; without it, the bind throws and the conflict
    /// is visible to the caller instead.
    /// </summary>
    public static LanSession ForHost(int port, Func<DateTime> clock)
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
            // Finding 3: the bind failed, so this socket holds no port and
            // cannot block the other instance - but leaving it undisposed
            // still leaks a handle per failed attempt (e.g. a player
            // clicking "Create a room" repeatedly while another instance is
            // already hosting).
            socket.Dispose();
            throw;
        }
        // The port the socket actually got, not the one that was asked for.
        // They are the same for a host on its well-known port, and they are
        // not when the caller passes 0 to mean "any" - and BoundPort exists
        // precisely so a caller need not know which case it is in. A test
        // pairing two sessions on ephemeral ports sent everything to port
        // zero before this.
        var bound = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        return new LanSession(socket, bound, bound, clock, hosting: true);
    }

    /// <summary>
    /// Binds port 0, so the OS assigns a free ephemeral port - two clients
    /// on the same machine each get their own, so they never collide with
    /// each other or with a host's well-known port. Sends are addressed to
    /// <paramref name="hostPort"/>, the host's well-known port, not this
    /// socket's own.
    /// </summary>
    public static LanSession ForClient(int hostPort, Func<DateTime> clock)
    {
        var socket = new UdpClient
        {
            Client = { ReceiveTimeout = 1 },
        };
        try
        {
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
        catch
        {
            // Finding 3: same shape as ForHost's - see there.
            socket.Dispose();
            throw;
        }
        var boundPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        return new LanSession(socket, boundPort, hostPort, clock, hosting: false);
    }

    /// <summary>
    /// The port actually bound, so callers (and tests) never have to
    /// hardcode it. Captured once at bind time rather than read from the
    /// socket on every call: <see cref="Dispose"/> disposes the socket too,
    /// and unlike every other public member here this one needs a value to
    /// hand back rather than simply doing nothing, so it cannot just check
    /// <c>_disposed</c> and return early the way they do (Finding 6).
    /// </summary>
    public int BoundPort => _boundPort;

    /// <summary>
    /// How many datagrams are waiting to be read. Only a test uses it, to tell
    /// "the message was rejected" from "the message had not arrived yet" -
    /// without which a test that expects rejection passes on an empty socket.
    /// </summary>
    internal int Available => _disposed ? 0 : _socket.Available;

    /// <summary>
    /// Tells the socket about a player it should pass places on to. The lobby
    /// learns these by being spoken to; a test has no lobby, so it says so
    /// directly.
    /// </summary>
    internal void KnowsAbout(IPEndPoint player) => _known.Add(player);

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
    /// <summary>
    /// Marks a datagram as belonging to the race start rather than the lobby.
    ///
    /// The two lobby formats both reject what they do not recognise, so a
    /// start message passes through them untouched and theirs through this.
    /// That keeps the start handshake off the wire format the lobby depends
    /// on, which is already carrying rooms, players and ready flags.
    /// </summary>
    const byte StartMagic = 0xA5;

    /// <summary>
    /// The host says two different things over the race's lifetime, and they
    /// are two different messages - which they were not.
    ///
    /// Leaving the lobby and beginning the race were both A5 02, and the flag
    /// either raised was never lowered between them. A measured run says what
    /// that costs: the client reached the barrier with the lobby's go already
    /// in hand and released itself one millisecond later, and the host -
    /// arriving 2.1s behind - was satisfied by the single report that client
    /// had sent on its way past. Two machines, one barrier, no wait, and a
    /// race that began 2.111 seconds apart.
    ///
    /// Three is skipped because <see cref="Place"/> already has it under this
    /// same magic. The two control messages are two bytes and a place is
    /// twenty-one, so the length would tell them apart anyway - but a reader
    /// that forgot to check would raise the start on the first car that moved.
    /// </summary>
    const byte AtTheLine = 1;      // a player has the race loaded and is holding
    const byte LeaveTheLobby = 2;  // the host says the race is on - come to the race
    const byte StartTheRace = 4;   // the host says everyone is here - begin now

    /// <summary>Where each player holding at the line came from, so the start can reach them.</summary>
    readonly HashSet<IPEndPoint> _atTheLine = [];

    /// <summary>
    /// Everyone the host has heard from in the lobby.
    ///
    /// Go has to reach the clients before any of them has reported at the line
    /// - that is the message telling them to leave the lobby and go to the
    /// race - so it cannot be sent only to those who have. These are the
    /// addresses the lobby itself learned.
    /// </summary>
    readonly HashSet<IPEndPoint> _known = [];

    /// <summary>How many players are holding at the line, the host included.</summary>
    public int WaitingAtTheLine => _atTheLine.Count + 1;

    /// <summary>
    /// Opens a fresh start line, so nothing said before this moment can
    /// satisfy the barrier that begins now.
    ///
    /// Three things are forgotten and all three have been seen to matter. The
    /// reports, because a player who was at the previous line is not thereby
    /// at this one. The start, for the same reason. And whatever is already
    /// sitting in the socket, because a datagram that arrived before the line
    /// opened was answering a question nobody had asked yet - without this the
    /// first collect would undo the reset.
    ///
    /// Dropping live reports costs nothing: a player at the line repeats
    /// theirs every frame until it is answered, so the host hears again within
    /// a frame, and what it then hears is that they are there now rather than
    /// that they once were.
    /// </summary>
    public void OpenTheStartLine()
    {
        if (_disposed) return;

        _atTheLine.Clear();
        HostSaidStartTheRace = false;

        for (int i = 0; i < MaxDatagramsPerTick && _socket.Available > 0; i++)
        {
            IPEndPoint? from = null;
            try
            {
                _socket.Receive(ref from);
            }
            catch (SocketException)
            {
                return;
            }
        }
    }

    /// <summary>Tells the host this machine has the race loaded and is holding.</summary>
    public void ReportAtTheLine(IPAddress hostAddress)
    {
        if (_disposed) return;
        Send([StartMagic, AtTheLine], new IPEndPoint(hostAddress, _hostPort));
    }

    /// <summary>
    /// Drains the socket, noting who has reported in. Lobby traffic still
    /// arriving is dropped: the room is settled by now, and answering it would
    /// only reopen a negotiation that is over.
    /// </summary>
    public void CollectAtTheLine()
    {
        if (_disposed) return;

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

            if (data.Length >= 2 && data[0] == StartMagic && data[1] == AtTheLine && from != null)
                _atTheLine.Add(from);
        }
    }

    /// <summary>
    /// Tells everyone the host has heard from that the race is on and the
    /// lobby is over. Addressed to the lobby's own addresses because at that
    /// moment nobody has reached a line yet - this is the message that sends
    /// them to one.
    /// </summary>
    public void SendLeaveTheLobby()
    {
        if (_disposed) return;
        foreach (var player in _known.Union(_atTheLine)) Send([StartMagic, LeaveTheLobby], player);
    }

    /// <summary>
    /// Releases everyone holding at the line. A different message from
    /// <see cref="SendLeaveTheLobby"/> on purpose - see the codes above for
    /// what happened while they were the same one.
    /// </summary>
    public void SendStartTheRace()
    {
        if (_disposed) return;
        foreach (var player in _known.Union(_atTheLine)) Send([StartMagic, StartTheRace], player);
    }

    /// <summary>
    /// Whether the host has ended the lobby. Raised by <see cref="ClientTick"/>,
    /// which is the only thing reading the socket while the lobby runs - the
    /// client needs every room announcement it is sent, and a second reader
    /// would swallow them.
    /// </summary>
    public bool HostSaidLeaveTheLobby { get; private set; }

    /// <summary>
    /// Forgets that the host once said to leave the lobby.
    ///
    /// It is what took every client out of the lobby and into the race. Left
    /// standing, it takes them straight back out the moment the race ends and
    /// the room reopens, before anybody has chosen anything.
    /// </summary>
    public void ForgetTheLobbyWasLeft() => HostSaidLeaveTheLobby = false;

    /// <summary>
    /// Whether the host has released the line. Lowered by
    /// <see cref="OpenTheStartLine"/> and raised only by
    /// <see cref="CollectTheStart"/>, so it says something about this race
    /// rather than about the lobby that led to it.
    /// </summary>
    public bool HostSaidStartTheRace { get; private set; }

    /// <summary>
    /// Looks for the host's start, for a client holding at the line.
    ///
    /// Nothing calls <see cref="ClientTick"/> by this point, so a barrier
    /// relying on it would wait for a flag nobody can raise. Room
    /// announcements no longer matter either - the room is settled and the
    /// race is loaded - so swallowing them costs nothing, which is what makes
    /// a second reader safe here and not during the lobby.
    /// </summary>
    public void CollectTheStart()
    {
        if (_disposed) return;

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

            if (data.Length == 2 && data[0] == StartMagic && data[1] == StartTheRace)
                HostSaidStartTheRace = true;
        }
    }

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

            if (from != null) _known.Add(from);

            if (intent.Leaving)
                session.ApplyClientLeave(intent.Name);
            else
                session.ApplyClientIntent(intent.Name, intent.Car, intent.Ready, intent.Colour,
                intent.Watching);

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

            if (data.Length >= 2 && data[0] == StartMagic && data[1] == LeaveTheLobby)
            {
                HostSaidLeaveTheLobby = true;
                continue;
            }

            if (!RoomState.TryDeserialise(data, out var room)) continue;
            session.OnRemoteState(room);
        }

        if (session.Current is not { } current) return;

        var now = _clock();
        if (_lastIntentSent is { } last && now - last < IntentInterval) return;

        var self = current.Players.FirstOrDefault(p => p.Name == session.PlayerName);
        var intent = new ClientIntent(current.Id, session.PlayerName,
            self?.Car ?? "", self?.Ready ?? false, Leaving: false, Colour: self?.Colour ?? 0,
            Watching: self?.Watching ?? false);
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
            self?.Car ?? "", self?.Ready ?? false, Leaving: true, Colour: self?.Colour ?? 0,
            Watching: self?.Watching ?? false);
        SendIntent(intent, hostAddress);
    }

    void SendIntent(ClientIntent intent, IPAddress hostAddress) =>
        Send(Serialise(intent), new IPEndPoint(hostAddress, _hostPort));

    void SendRoomState(Room room, IPEndPoint to) =>
        Send(RoomState.Serialise(room), to);

    /// <summary>Where a car is, as one player says it is.</summary>
    const byte Place = 3;

    /// <summary>
    /// How wide a place message is: the magic, the kind, whose it is, three
    /// coordinates as words, and three angles as shorts.
    ///
    /// The nine-word transform this used to carry held the rotation matrix as
    /// well, which was six words of nothing: the game rebuilds that matrix
    /// every frame from the three angles, so sending it was sending an answer
    /// the receiver was about to work out again. The angles are what one
    /// machine cannot work out about another's car - a car that is sliding
    /// points one way and moves another, and only its owner knows which.
    /// </summary>
    const int PlaceWords = 3;
    const int PlaceAngles = 3;
    const int PlaceBytes = 3 + PlaceWords * 4 + PlaceAngles * 2;

    /// <summary>
    /// Where every other player says their car is, by their seat in the room.
    ///
    /// Keyed by the room's own ordering rather than by address or by name,
    /// because that ordering is the one thing every machine already agrees on:
    /// the host published it, and each machine rotates its own player to the
    /// front of it locally. A seat number survives that rotation; a slot
    /// number would not.
    /// </summary>
    readonly Dictionary<byte, RemoteCars.Pose> _places = [];

    public IReadOnlyDictionary<byte, RemoteCars.Pose> Places => _places;

    /// <summary>Tells everyone where this machine's car is and which way it faces.</summary>
    public void SendPlace(byte seat, RemoteCars.Pose pose, IPAddress? host = null)
    {
        if (_disposed) return;

        var data = new byte[PlaceBytes];
        data[0] = StartMagic;
        data[1] = Place;
        data[2] = seat;
        BitConverter.TryWriteBytes(data.AsSpan(3), pose.Place.X);
        BitConverter.TryWriteBytes(data.AsSpan(7), pose.Place.Z);
        BitConverter.TryWriteBytes(data.AsSpan(11), pose.Place.Y);
        BitConverter.TryWriteBytes(data.AsSpan(15), pose.AroundX);
        BitConverter.TryWriteBytes(data.AsSpan(17), pose.AroundY);
        BitConverter.TryWriteBytes(data.AsSpan(19), pose.AroundZ);

        if (host is not null) Send(data, new IPEndPoint(host, _hostPort));
        foreach (var player in _known.Union(_atTheLine)) Send(data, player);
    }

    /// <summary>
    /// Drains the socket, keeping the latest place from each seat.
    ///
    /// The latest, not every one: a place is a snapshot and an old one is of no
    /// use to anybody. Anything that is not a place is dropped - the room is
    /// settled by the time cars are moving, and answering lobby traffic now
    /// would reopen a negotiation that is over.
    /// </summary>
    public void CollectPlaces()
    {
        if (_disposed) return;

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

            if (data.Length < PlaceBytes || data[0] != StartMagic || data[1] != Place) continue;

            // The host is the only machine every other machine can reach, so it
            // is the only one that can pass a car on. A client's places go to
            // the host and nowhere else - _known and _atTheLine are filled by
            // host code, and on a client both are empty - so without this, two
            // players work by accident and three do not: the second client
            // never hears the first, its car is never given a place, and the
            // game's own driver takes it over.
            Relay(data, from);

            _places[data[2]] = new RemoteCars.Pose(
                new RemoteCars.Place(
                    BitConverter.ToInt32(data, 3),
                    BitConverter.ToInt32(data, 7),
                    BitConverter.ToInt32(data, 11)),
                BitConverter.ToInt16(data, 15),
                BitConverter.ToInt16(data, 17),
                BitConverter.ToInt16(data, 19));
        }
    }

    /// <summary>
    /// Passes a place on to every other machine, when this one is the host.
    ///
    /// A client's socket knows one address - the host's - and learning the
    /// others would mean a second round of discovery among peers, six ways for
    /// six players, through whatever each machine's network will allow. The
    /// host already has every address, because every client has already spoken
    /// to it. Passing the datagram along unchanged costs one send per other
    /// player and needs nobody to learn anything.
    ///
    /// Unchanged on purpose: the seat it is keyed by is the room's, so it means
    /// the same thing to every machine, and a relayed place is indistinguishable
    /// from a first-hand one. That also makes the relay idempotent - a client
    /// cannot tell, and does not need to.
    /// </summary>
    void Relay(byte[] data, IPEndPoint? from)
    {
        if (!_hosting) return;

        foreach (var player in _known.Union(_atTheLine))
        {
            if (from is not null && player.Equals(from)) continue;
            Send(data, player);
        }
    }

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

    internal readonly record struct ClientIntent(
        Guid RoomId, string Name, string Car, bool Ready, bool Leaving,
        byte Colour = 0, bool Watching = false);

    internal static byte[] Serialise(ClientIntent intent)
    {
        var buffer = new List<byte>();
        buffer.AddRange(Magic);
        buffer.Add(Version);
        buffer.AddRange(intent.RoomId.ToByteArray());

        byte flags = 0;
        if (intent.Ready) flags |= ReadyFlag;
        if (intent.Leaving) flags |= LeavingFlag;
        if (intent.Watching) flags |= WatchingFlag;
        buffer.Add(flags);

        WriteString(buffer, intent.Name);
        WriteString(buffer, intent.Car);
        buffer.Add(intent.Colour);
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
        if (!TryByte(span, ref offset, out byte colour)) return false;

        intent = new ClientIntent(roomId, name, car,
            Ready: (flags & ReadyFlag) != 0, Leaving: (flags & LeavingFlag) != 0,
            Colour: colour, Watching: (flags & WatchingFlag) != 0);
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
