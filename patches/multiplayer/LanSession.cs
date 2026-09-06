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
    const byte Version = 3;
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

    readonly IGameLink _link;
    bool _ownsTheLink = true;
    readonly int _boundPort;
    readonly int _hostPort;

    /// <summary>
    /// Whether this socket is the host's. Until the places had to be passed on,
    /// only the caller needed to know which role it had built - the two factory
    /// methods differ in how they bind and in nothing else. The relay is the
    /// first thing the socket itself has to decide.
    /// </summary>
    readonly bool _hosting;

    /// <summary>
    /// What this client says to be let in. Empty on a host, which never has to
    /// ask itself anything.
    /// </summary>
    internal string Secret { get; set; } = "";
    readonly Func<DateTime> _clock;
    DateTime? _lastIntentSent;
    bool _disposed;

    LanSession(IGameLink link, int boundPort, int hostPort, Func<DateTime> clock, bool hosting)
    {
        _link = link;
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
        // The bind, and the reason for not reusing the address, now live in
        // DirectLink - see there. What is still decided here is the pair of
        // ports: a host answers on the one it bound and is addressed on the
        // same, which is what makes it well known.
        //
        // And it is the port the link actually got, not the one asked for.
        // They are the same for a host on its well-known port and not when a
        // caller passes 0 to mean "any"; BoundPort exists precisely so a
        // caller need not know which case it is in. A test pairing two
        // sessions on ephemeral ports sent everything to port zero before
        // this.
        var link = DirectLink.Bind(port);
        return new LanSession(link, link.BoundPort, link.BoundPort, clock, hosting: true);
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
        var link = DirectLink.Ephemeral();
        return new LanSession(link, link.BoundPort, hostPort, clock, hosting: false);
    }

    /// <summary>
    /// A session over a link somebody else built - which in practice means a
    /// relayed one. The two factories above are the local-network cases and
    /// build their own socket; this is the seam for everything that reaches a
    /// host some other way.
    /// </summary>
    /// <param name="ownsTheLink">
    /// Whether disposing the session should dispose the link. False for a
    /// relayed one: that link is also the room list and the keepalive, it
    /// outlives any single session, and closing it here would drop the NAT
    /// mapping the whole arrangement rests on.
    /// </param>
    public static LanSession Over(IGameLink link, int hostPort, Func<DateTime> clock,
                                  bool hosting, bool ownsTheLink = true) =>
        new(link, link.BoundPort, hostPort, clock, hosting) { _ownsTheLink = ownsTheLink };

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
    internal int Available => _disposed ? 0 : _link.Available;

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
    /// How many knocks have gone out, and how many datagrams have come back.
    ///
    /// A knock that is never answered says nothing by itself - UDP has no
    /// failure to report, so a blocked port, a wrong address, a host that is
    /// not running and a network that is fine all look identical from here.
    /// These two do separate them: knocks rising with nothing heard means the
    /// datagrams are not getting there or not getting back, while anything
    /// heard at all means the round trip works and the argument is about what
    /// was said.
    /// </summary>
    public int KnocksSent { get; private set; }
    public int DatagramsHeard { get; private set; }

    /// <summary>Who spoke last, which is not always who was spoken to.</summary>
    public IPEndPoint? LastHeardFrom { get; private set; }

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
    /// <summary>
    /// The host says everyone is here, and how long from now the race begins.
    ///
    /// A deadline rather than a starting pistol. "Begin now" means begin when
    /// this arrives, which is a different moment on every machine - one flight
    /// time apart on a local network and a tenth of a second apart across a bad
    /// one. Saying "begin in N milliseconds" instead makes it not matter which
    /// of them arrives: the number is computed fresh at every send, so a
    /// machine that hears only the last of them works out the same instant as
    /// one that heard them all.
    ///
    /// What it does not do on its own is take the flight out. Every one of
    /// these is stamped when the host sends and applied when the client
    /// receives, so all of them are late by the same amount and no later one
    /// corrects it. The echoed token does that - see <see cref="EchoBytes"/>.
    /// </summary>
    const byte StartTheRace = 4;

    /// <summary>The magic, the kind, and the milliseconds left as a halfword.</summary>
    const int StartBytes = 4;

    /// <summary>
    /// And the token the host echoes after it, which is what makes the
    /// deadline mean the same instant on both machines.
    ///
    /// "Begin in N milliseconds" is computed when the host sends and applied
    /// when the client receives, so the client's instant is later than the
    /// host's by however long the datagram took - every time, and by the same
    /// amount, so no later message corrects it. On a local network that is
    /// half a millisecond and nobody could see it. Through a relay and a
    /// tunnel it is tens of milliseconds, which at racing speed is a car
    /// length of head start for whoever is hosting.
    ///
    /// The client stamps each report with a token; the host hands that
    /// player's own token back; the client sees its own report return and
    /// knows the round trip. Half of it is what to take off the deadline.
    /// </summary>
    const int EchoBytes = StartBytes + 2;

    /// <summary>The magic, the kind, and the token, for a report at the line.</summary>
    const int AtTheLineBytes = 4;

    /// <summary>
    /// A ceiling on the correction, because a token that came back absurdly
    /// late is a token that waited in a buffer rather than one that measured a
    /// path. Half a second of round trip is already a connection nobody can
    /// race over; beyond it the measurement is likelier to be wrong than the
    /// latency is to be real.
    /// </summary>
    static readonly TimeSpan LongestWorthTrusting = TimeSpan.FromMilliseconds(500);

    /// <summary>What this machine stamped on each report, and when (client).</summary>
    readonly Dictionary<ushort, DateTime> _reported = [];

    /// <summary>
    /// Tokens count from one, because zero is what the host sends when it has
    /// no token for a player - a client that had stamped a report zero would
    /// read that as its own report coming back and measure a round trip that
    /// began whenever it happened to start reporting.
    /// </summary>
    ushort _nextToken = 1;

    /// <summary>The last token each player at the line sent (host).</summary>
    readonly Dictionary<IPEndPoint, ushort> _tokenOf = [];

    /// <summary>
    /// The round trip the start was corrected by, or null if nothing measured
    /// one. Reported rather than assumed: a start that is still out wants to
    /// say whether it knew the latency and by how much.
    /// </summary>
    public TimeSpan? MeasuredRoundTrip { get; private set; }

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
        _tokenOf.Clear();
        _reported.Clear();
        MeasuredRoundTrip = null;
        StartsAt = null;

        for (int i = 0; i < MaxDatagramsPerTick && _link.Available > 0; i++)
        {
            IPEndPoint? from = null;
            try
            {
                _link.Receive(ref from);
            }
            catch (SocketException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Tells the host this machine has the race loaded and is holding, and
    /// stamps it so the answer can be timed.
    ///
    /// The stamp is remembered rather than sent as a time: the two clocks are
    /// minutes apart - see <see cref="StartsAt"/> - so only a duration this
    /// machine measures against itself means anything.
    /// </summary>
    public void ReportAtTheLine(IPAddress hostAddress)
    {
        if (_disposed) return;

        ushort token = _nextToken++;
        if (_nextToken == 0) _nextToken = 1;
        _reported[token] = _clock();

        // Bounded, because a client can report for the whole of StartPatience
        // and a map that only grows is a map that outlives the race. The
        // oldest are the ones whose answer is never coming.
        if (_reported.Count > 256)
        {
            var oldest = _reported.OrderBy(e => e.Value).First().Key;
            _reported.Remove(oldest);
        }

        Send([StartMagic, AtTheLine, (byte)(token & 0xFF), (byte)(token >> 8)],
            _link.HostAt(hostAddress, _hostPort));
    }

    /// <summary>
    /// Drains the socket, noting who has reported in. Lobby traffic still
    /// arriving is dropped: the room is settled by now, and answering it would
    /// only reopen a negotiation that is over.
    /// </summary>
    public void CollectAtTheLine()
    {
        if (_disposed) return;

        for (int i = 0; i < MaxDatagramsPerTick && _link.Available > 0; i++)
        {
            IPEndPoint? from = null;
            byte[] data;
            try
            {
                data = _link.Receive(ref from);
            }
            catch (SocketException)
            {
                return;
            }

            if (data.Length >= 2 && data[0] == StartMagic && data[1] == AtTheLine && from != null)
            {
                _atTheLine.Add(from);

                // An older client sends two bytes and no token. It is still at
                // the line and still counted; it simply cannot be told what
                // its own latency was, and starts the way it always did.
                if (data.Length >= AtTheLineBytes)
                    _tokenOf[from] = (ushort)(data[2] | (data[3] << 8));
            }
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
    public void SendStartTheRace(int millisecondsFromNow)
    {
        if (_disposed) return;
        ushort left = (ushort)Math.Clamp(millisecondsFromNow, 0, ushort.MaxValue);

        // One datagram per player rather than one for everybody, because what
        // is appended is that player's own token - a shared message could
        // carry only one of them, and a token belonging to somebody else
        // measures nothing.
        foreach (var player in _known.Union(_atTheLine))
        {
            ushort token = _tokenOf.TryGetValue(player, out var mine) ? mine : (ushort)0;
            Send(
                [StartMagic, StartTheRace, (byte)(left & 0xFF), (byte)(left >> 8),
                 (byte)(token & 0xFF), (byte)(token >> 8)],
                player);
        }
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
    /// <summary>
    /// When the race begins, by this machine's own clock, or null until the
    /// host has said.
    ///
    /// Local, and it has to be: the two machines' clocks are minutes apart -
    /// one of these logs reads 16:06 while the other reads 16:08 for the same
    /// race - so an instant one of them names means nothing to the other. A
    /// duration does.
    /// </summary>
    public DateTime? StartsAt { get; private set; }

    /// <summary>Whether the host has said anything about starting yet.</summary>
    public bool HostSaidStartTheRace => StartsAt is not null;

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

        for (int i = 0; i < MaxDatagramsPerTick && _link.Available > 0; i++)
        {
            IPEndPoint? from = null;
            byte[] data;
            try
            {
                data = _link.Receive(ref from);
            }
            catch (SocketException)
            {
                return;
            }

            if (data.Length < StartBytes || data[0] != StartMagic || data[1] != StartTheRace)
                continue;

            int left = data[2] | (data[3] << 8);
            var now = _clock();

            if (data.Length >= EchoBytes)
            {
                ushort token = (ushort)(data[4] | (data[5] << 8));
                if (token != 0 && _reported.TryGetValue(token, out var sent))
                {
                    var roundTrip = now - sent;

                    // The smallest seen, not the latest. The host echoes the
                    // last report it read, and keeps echoing it until a newer
                    // one arrives - so most of these measure how long ago that
                    // report was sent rather than how long the path takes, and
                    // they get worse the longer the countdown runs. A round
                    // trip can only be inflated by waiting, never shortened by
                    // it, so the smallest is the closest to the truth. It is
                    // what ping and every clock synchronisation take, for this
                    // reason.
                    if (roundTrip > TimeSpan.Zero
                        && roundTrip <= LongestWorthTrusting
                        && roundTrip < (MeasuredRoundTrip ?? TimeSpan.MaxValue))
                        MeasuredRoundTrip = roundTrip;
                }
            }

            // Half the best measurement so far, because what is wanted is how
            // long the host's word took to arrive and the two directions are
            // assumed alike - the same assumption every clock synchronisation
            // makes, and wrong by far less than not correcting at all.
            var flight = MeasuredRoundTrip is { } best ? best / 2 : TimeSpan.Zero;

            // The latest left is the freshest: each carries what remained when
            // it was sent, so a later one has crossed less of the wait.
            StartsAt = now + TimeSpan.FromMilliseconds(left) - flight;
        }
    }

    public void HostTick(Session session)
    {
        if (_disposed) return;
        if (session.Phase != SessionPhase.Hosting) return;
        if (session.Current is not { } room) return;

        for (int i = 0; i < MaxDatagramsPerTick && _link.Available > 0; i++)
        {
            IPEndPoint? from = null;
            byte[] data;
            try
            {
                data = _link.Receive(ref from);
            }
            catch (SocketException)
            {
                return;
            }

            DatagramsHeard++;
            LastHeardFrom = from;

            if (KeptAResult(data, from)) continue;

            if (!TryDeserialise(data, out var intent)) continue;
            if (!SaidTheSecret(room.Secret, intent.Secret)) continue;

            // Guid.Empty is a knock: a client that reached this host by address
            // has never heard the room announced and cannot name it. Anything
            // else naming the wrong room is crossed wires or forgery.
            if (intent.RoomId != Guid.Empty && intent.RoomId != room.Id) continue;

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

        // Knocking drains too, and that is the whole of it: a knock is answered
        // with room state, Session.OnRemoteState already knows how to adopt one
        // while knocking, and this is the only thing that ever reads the
        // socket. Refusing anything but Joined meant the answer arrived, sat in
        // the receive buffer, and was thrown away when the client gave up -
        // "Knocking..." forever, on a network that was working perfectly.
        //
        // The half below sends nothing while knocking: it needs a room, and a
        // knocking client has none until this drain gives it one. ModeHook
        // repeats the knock itself.
        if (session.Phase is not (SessionPhase.Joined or SessionPhase.Knocking)) return;

        for (int i = 0; i < MaxDatagramsPerTick && _link.Available > 0; i++)
        {
            IPEndPoint? from = null;
            byte[] data;
            try
            {
                data = _link.Receive(ref from);
            }
            catch (SocketException)
            {
                return;
            }

            DatagramsHeard++;
            LastHeardFrom = from;

            if (data.Length >= 2 && data[0] == StartMagic && data[1] == LeaveTheLobby)
            {
                HostSaidLeaveTheLobby = true;
                continue;
            }

            if (KeptAResult(data, from)) continue;

            if (!RoomState.TryDeserialise(data, out var room)) continue;
            session.OnRemoteState(room);
        }

        if (session.Current is not { } current) return;

        var now = _clock();
        if (_lastIntentSent is { } last && now - last < IntentInterval) return;

        var self = current.Players.FirstOrDefault(p => p.Name == session.PlayerName);
        var intent = new ClientIntent(current.Id, session.PlayerName,
            self?.Car ?? "", self?.Ready ?? false, Leaving: false, Colour: self?.Colour ?? 0,
            Watching: self?.Watching ?? false, Secret: Secret);
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
            Watching: self?.Watching ?? false, Secret: Secret);
        SendIntent(intent, hostAddress);
    }

    void SendIntent(ClientIntent intent, IPAddress hostAddress) =>
        Send(Serialise(intent), _link.HostAt(hostAddress, _hostPort));

    void SendRoomState(Room room, IPEndPoint to) =>
        Send(RoomState.Serialise(room), to);

    /// <summary>Where a car is, as one player says it is.</summary>
    const byte Place = 3;

    /// <summary>
    /// How one player's race ended, as only their own machine can say.
    ///
    /// Every machine teleports every other car here every frame, so this
    /// machine's idea of when somebody else crossed the line is a fact about
    /// the network. A driver's own machine is the only one that raced them.
    /// </summary>
    const byte Result = 5;

    /// <summary>
    /// The magic, the kind, whose it is, their laps, their time, their best lap.
    /// </summary>
    const int ResultBytes = 3 + 1 + 4 + 4;

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

    /// <summary>
    /// And one angle per wheel, which is the other thing a machine cannot work
    /// out about somebody else's car: the game derives a wheel's angle from the
    /// car's own physics, and a car this port teleports has none worth the
    /// name.
    /// </summary>
    const int PlaceWheels = RemoteCars.WheelsOnACar;

    /// <summary>
    /// And a counter, so a place that arrives late can be told from one that
    /// is new.
    ///
    /// UDP reorders, and a relay and a tunnel in the path make it likelier
    /// still. Without this the newest place was whichever arrived last, so an
    /// overtaken datagram put the car back where it had been and the next one
    /// snapped it forward again - a car jumping about on a connection that had
    /// not lost anything at all.
    /// </summary>
    const int PlaceCounter = 2;

    const int PlaceBytes =
        3 + PlaceWords * 4 + PlaceAngles * 2 + PlaceWheels * 2 + PlaceCounter;

    /// <summary>Where the counter sits, which is after everything that was there before.</summary>
    const int PlaceCounterAt = 3 + PlaceWords * 4 + PlaceAngles * 2 + PlaceWheels * 2;

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

    /// <summary>This machine's own count, one higher on every place it sends.</summary>
    ushort _placesSent;

    /// <summary>The newest count seen from each seat, and how many have been refused since.</summary>
    readonly Dictionary<byte, (ushort Newest, int Refused)> _placeCount = [];

    /// <summary>
    /// How many refusals in a row mean the other machine started counting
    /// again rather than that this one is being overtaken.
    ///
    /// A player who leaves and comes back builds a new session, whose counter
    /// starts at zero - and zero is a very old number. Without this the host
    /// would refuse that player's every place until their counter climbed back
    /// past where it left off, which at thirty a second is half an hour of a
    /// car that does not move.
    ///
    /// Ten, because reordering moves a datagram past a handful of its
    /// neighbours and not past ten in a row. A run that long is not a race
    /// condition, it is a different sender.
    /// </summary>
    const int RefusalsThatMeanARestart = 10;

    /// <summary>
    /// Whether a count is newer than the last one seen from that seat.
    ///
    /// Half the space is "ahead" and half is "behind", so the comparison keeps
    /// working when the counter wraps - at thirty places a second it wraps
    /// every thirty-six minutes, which a long evening reaches.
    /// </summary>
    internal static bool IsNewer(ushort incoming, ushort newest) =>
        (ushort)(incoming - newest) is > 0 and < 32768;

    /// <summary>What each seat's own machine said their race ended as.</summary>
    readonly Dictionary<byte, RaceResult.Finish> _results = [];

    public IReadOnlyDictionary<byte, RaceResult.Finish> Results => _results;

    /// <summary>
    /// Asks a host at a known address to be let in, without knowing anything
    /// about its room - not even its id.
    ///
    /// It is an ordinary intent with an empty room id, so the host needs no new
    /// message to understand one and answers it the way it answers every
    /// intent: with room state.
    /// </summary>
    public void SendKnock(IPEndPoint host, string name, string secret)
    {
        if (_disposed) return;

        Secret = secret;
        var knock = Serialise(new ClientIntent(
            Guid.Empty, name, "", Ready: false, Leaving: false, Secret: secret));

        KnocksSent++;
        Send(knock, host);
    }

    /// <summary>Tells everyone how this machine's driver got on.</summary>
    public void SendResult(byte seat, RaceResult.Finish finish, IPAddress? host = null)
    {
        if (_disposed) return;

        var data = new byte[ResultBytes];
        data[0] = StartMagic;
        data[1] = Result;
        data[2] = seat;
        data[3] = (byte)Math.Clamp(finish.Laps, 0, 255);
        BitConverter.TryWriteBytes(data.AsSpan(4), finish.Milliseconds);
        BitConverter.TryWriteBytes(data.AsSpan(8), finish.BestLapMilliseconds);

        _results[seat] = finish;

        if (host is not null) Send(data, _link.HostAt(host, _hostPort));
        foreach (var player in _known.Union(_atTheLine)) Send(data, player);
    }

    /// <summary>
    /// Drains the socket for results, keeping the first from each seat.
    ///
    /// The first rather than the latest, which is the opposite of a place: a
    /// place is a snapshot and an old one is worthless, while a result is final
    /// the moment it is sent and is repeated only in case a datagram was lost.
    ///
    /// **Not to be called while the lobby is running.** Like every drain on
    /// this socket it throws away what it is not looking for, and in the lobby
    /// that is the clients' own intents: called from the lobby loop, it stopped
    /// the host hearing its clients, which dropped them for going quiet and
    /// made the room impossible to stay in. The lobby's loops keep results
    /// themselves - see <see cref="KeptAResult"/> - so nothing there needs
    /// this.
    /// </summary>
    public void CollectResults()
    {
        if (_disposed) return;

        for (int i = 0; i < MaxDatagramsPerTick && _link.Available > 0; i++)
        {
            IPEndPoint? from = null;
            byte[] data;
            try
            {
                data = _link.Receive(ref from);
            }
            catch (SocketException)
            {
                return;
            }

            KeptAResult(data, from);
        }
    }

    /// <summary>Forgets the last race's results, so the next race collects its own.</summary>
    public void ForgetTheResults() => _results.Clear();

    /// <summary>
    /// Takes a result out of a datagram that was being read for something else.
    ///
    /// The lobby's own two loops drain this same socket and discard whatever
    /// they do not recognise, so without this a result that arrived on their
    /// turn rather than on CollectResults' turn was simply eaten - and which
    /// turn it lands on is a coin toss sixty times a second. Returns whether
    /// the datagram was a result, so the caller can stop looking at it.
    /// </summary>
    bool KeptAResult(byte[] data, IPEndPoint? from)
    {
        if (data.Length < ResultBytes || data[0] != StartMagic || data[1] != Result) return false;

        Relay(data, from);

        byte seat = data[2];
        if (!_results.ContainsKey(seat))
            _results[seat] = new RaceResult.Finish(
                data[3], BitConverter.ToInt32(data, 4), BitConverter.ToInt32(data, 8));

        return true;
    }

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
        for (int wheel = 0; wheel < PlaceWheels; wheel++)
            BitConverter.TryWriteBytes(data.AsSpan(21 + wheel * 2), pose.Wheels[wheel]);

        // One per place sent, not one per frame or per second: what the far
        // side needs is an order, and every place this machine sends is one
        // step along it.
        BitConverter.TryWriteBytes(data.AsSpan(PlaceCounterAt), _placesSent++);

        if (host is not null) Send(data, _link.HostAt(host, _hostPort));
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

        for (int i = 0; i < MaxDatagramsPerTick && _link.Available > 0; i++)
        {
            IPEndPoint? from = null;
            byte[] data;
            try
            {
                data = _link.Receive(ref from);
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

            byte seat = data[2];
            ushort count = BitConverter.ToUInt16(data, PlaceCounterAt);

            if (_placeCount.TryGetValue(seat, out var seen))
            {
                if (!IsNewer(count, seen.Newest))
                {
                    // Overtaken on the way here, and the car is already
                    // somewhere newer than this - unless it keeps happening,
                    // which means the other machine started counting again.
                    if (seen.Refused + 1 < RefusalsThatMeanARestart)
                    {
                        _placeCount[seat] = (seen.Newest, seen.Refused + 1);
                        continue;
                    }
                }
            }

            _placeCount[seat] = (count, 0);

            _places[seat] = new RemoteCars.Pose(
                new RemoteCars.Place(
                    BitConverter.ToInt32(data, 3),
                    BitConverter.ToInt32(data, 7),
                    BitConverter.ToInt32(data, 11)),
                BitConverter.ToInt16(data, 15),
                BitConverter.ToInt16(data, 17),
                BitConverter.ToInt16(data, 19),
                new RemoteCars.Wheels(
                    BitConverter.ToInt16(data, 21),
                    BitConverter.ToInt16(data, 23),
                    BitConverter.ToInt16(data, 25),
                    BitConverter.ToInt16(data, 27)));
        }
    }

    /// <summary>How many places arrived out of order and were refused, for a test to count.</summary>
    internal int PlacesRefused => _placeCount.Values.Sum(x => x.Refused);

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
            _link.Send(data, data.Length, to);
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
        if (_ownsTheLink) _link.Dispose();
    }

    // ---- wire format ----

    internal readonly record struct ClientIntent(
        Guid RoomId, string Name, string Car, bool Ready, bool Leaving,
        byte Colour = 0, bool Watching = false, string Secret = "");

    /// <summary>
    /// Whether a client that said <paramref name="said"/> may join a room whose
    /// secret is <paramref name="roomSecret"/>.
    ///
    /// A room with no secret is open, which is what every room was before there
    /// was an address to reach one at. A room with one is closed to everything
    /// that does not repeat it exactly - and on a port that faces the internet,
    /// most of what arrives is a scanner rather than a player.
    /// </summary>
    internal static bool SaidTheSecret(string roomSecret, string said) =>
        roomSecret.Length == 0 || string.Equals(roomSecret, said, StringComparison.Ordinal);

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
        WriteString(buffer, intent.Secret);
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
        if (!TryString(span, ref offset, out string secret)) return false;

        intent = new ClientIntent(roomId, name, car,
            Ready: (flags & ReadyFlag) != 0, Leaving: (flags & LeavingFlag) != 0,
            Colour: colour, Watching: (flags & WatchingFlag) != 0, Secret: secret);
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
