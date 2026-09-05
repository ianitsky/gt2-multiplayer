using System.Net;

namespace GT2Port.Multiplayer;

/// <summary>
/// A host reachable two ways at once: addressed directly on this network, and
/// wrapped up through the relay from anywhere else.
///
/// It exists because a host had to choose. With a relay configured the session
/// was built over it and nothing bound the well-known port, so a player who
/// picked the room out of "rooms on this network" - the room the host was
/// announcing on that very network - sent every intent to a port nobody was
/// listening on and was dropped three seconds later for going quiet. Without a
/// relay the opposite held and nobody outside could reach the room at all. The
/// two lists are not two kinds of room; they are two ways of finding the same
/// one, and a host that answers on only one of them makes a liar of whichever
/// list it is not answering.
///
/// The rule is: reply the way you were spoken to. A player heard directly is
/// answered directly, which keeps a local race off the internet and out of the
/// relay's traffic bill; one heard through the relay is answered through it.
/// Nothing has to be configured and nothing has to guess, because by the time
/// there is anything to say back, that player has already said something.
/// </summary>
public sealed class EitherLink : IGameLink
{
    readonly IGameLink _direct;
    readonly IGameLink _relayed;

    /// <summary>
    /// Who was last heard through the relay. Kept as a set of endpoints rather
    /// than as a flag on a player, because this seam is below anything that
    /// knows what a player is - see <see cref="IGameLink"/>.
    /// </summary>
    readonly HashSet<IPEndPoint> _spokeThroughTheRelay = [];

    bool _disposed;

    /// <param name="direct">
    /// The socket on the well-known port. Owned: disposing this disposes it.
    /// </param>
    /// <param name="relayed">
    /// The relay link, which is borrowed. It is also the room list and the
    /// keepalive, it outlives any one session, and closing it here would drop
    /// the NAT mapping the whole arrangement rests on.
    /// </param>
    public EitherLink(IGameLink direct, IGameLink relayed)
    {
        _direct = direct;
        _relayed = relayed;
    }

    /// <summary>
    /// The directly bound port, because that is the one a player on this
    /// network addresses. The relay's own port is not something anybody types
    /// or announces.
    /// </summary>
    public int BoundPort => _direct.BoundPort;

    /// <summary>
    /// Both, and both are asked every time. Asking the relay is also what
    /// advances it - a relayed link only takes in what the server said when it
    /// is read - so a count that short-circuited on the direct socket having
    /// something would leave the relay's traffic sitting unread for as long as
    /// anybody on this network kept talking.
    /// </summary>
    public int Available => _disposed ? 0 : _direct.Available + _relayed.Available;

    /// <summary>
    /// The local network first, for no better reason than that it is the
    /// cheaper of the two to ask. Order does not otherwise matter: every
    /// datagram carries who sent it, and the lobby keys players by that rather
    /// than by arrival order.
    /// </summary>
    public byte[] Receive(ref IPEndPoint? from)
    {
        if (_direct.Available > 0)
        {
            var direct = _direct.Receive(ref from);

            // A player who used to reach us through the relay and is now
            // arriving directly has changed path - answering the old way would
            // send to an endpoint that may no longer be forwarded.
            if (from is not null) _spokeThroughTheRelay.Remove(from);
            return direct;
        }

        // Throws WouldBlock when it too has nothing, which is what every
        // caller of a link already handles.
        var relayed = _relayed.Receive(ref from);
        if (from is not null) _spokeThroughTheRelay.Add(from);
        return relayed;
    }

    /// <summary>
    /// The way that player last reached us. An endpoint nobody has been heard
    /// from goes directly, which is what a bare address means and what every
    /// caller built for itself before links existed.
    /// </summary>
    public void Send(byte[] data, int length, IPEndPoint to)
    {
        if (_disposed) return;

        if (_spokeThroughTheRelay.Contains(to)) _relayed.Send(data, length, to);
        else _direct.Send(data, length, to);
    }

    /// <summary>
    /// The direct answer, because this is a host and a host is not looking for
    /// one - it is being looked for. A client never holds one of these.
    /// </summary>
    public IPEndPoint HostAt(IPAddress address, int hostPort) =>
        _direct.HostAt(address, hostPort);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Only the socket this owns. The relay is borrowed - see the
        // constructor.
        _direct.Dispose();
    }
}
