namespace GT2Port.Multiplayer;

/// <summary>
/// One remote car's recent past, and where to draw it now.
///
/// A place used to be written to the car the moment it arrived, so the car
/// moved exactly as the network delivered: a step for every datagram, of
/// whatever size the gap happened to be, and nothing at all while one was
/// missing. On a good connection that is thirty even steps a second and looks
/// like driving. On a bad one it is the jumping.
///
/// So the places go into a short history with the sender's own timing on them,
/// and the car is drawn a little way behind the newest of them - far enough
/// back that there is always a later place to aim at, so every frame is a
/// point between two known ones rather than the last one that got through.
///
/// The cost is honest and worth saying: the car is shown where it was, not
/// where it is. <see cref="Delay"/> is how far back, and it follows the
/// connection - as little as forty milliseconds on a clean link, more when the
/// arrivals are ragged, never more than a quarter second.
///
/// The sender's clock and this machine's never agree - they are minutes apart
/// - so nothing here compares them. What travels is how long the sender waited
/// between its own two places, and those add up into a timeline that is the
/// sender's, measured against itself.
/// </summary>
public sealed class RemoteTrack
{
    /// <summary>
    /// The least a car is ever drawn behind. Below about a frame there is
    /// nothing between two places to interpolate and this becomes the old
    /// behaviour with extra arithmetic.
    /// </summary>
    public static readonly TimeSpan Least = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// And the most. Past a quarter of a second the car being drawn is far
    /// enough into the past to be somewhere a driver would not expect it, and
    /// a connection that needs more than this is one no amount of smoothing
    /// rescues.
    /// </summary>
    public static readonly TimeSpan Most = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How much of the measured raggedness to allow for. Three deviations
    /// covers nearly every arrival on a link whose jitter is anything like
    /// even, and the ones it does not cover are the ones the buffer running
    /// dry is meant to handle.
    /// </summary>
    const double Deviations = 3.0;

    /// <summary>
    /// How quickly the measurements follow a change. A twentieth each time
    /// takes a couple of seconds to settle, which is slow enough that one late
    /// datagram does not move the delay and quick enough that a connection
    /// going bad is allowed for before the race is over.
    /// </summary>
    const double Follows = 0.05;

    /// <summary>
    /// How long a history to keep. Twice the longest delay, so there is always
    /// something on both sides of where the car is being drawn.
    /// </summary>
    static readonly TimeSpan Remembered = TimeSpan.FromMilliseconds(500);

    readonly List<(double At, RemoteCars.Pose Pose)> _seen = [];

    /// <summary>Where the sender's own clock has reached, by its own reckoning.</summary>
    double _source;

    DateTime? _arrivedLast;

    double _interval;
    double _jitter;
    bool _measured;

    /// <summary>Where the car is being drawn, on the sender's timeline.</summary>
    double _drawnAt;
    DateTime? _drawnWhen;

    /// <summary>How far behind the newest place the car is being drawn.</summary>
    public TimeSpan Delay =>
        !_measured ? Least
        : TimeSpan.FromMilliseconds(Math.Clamp(
            _interval + Deviations * _jitter,
            Least.TotalMilliseconds,
            Most.TotalMilliseconds));

    /// <summary>How many places are being held, which a test counts.</summary>
    public int Held => _seen.Count;

    /// <summary>
    /// Takes a place, with how long the sender waited since its own previous
    /// one.
    /// </summary>
    public void Heard(int sinceTheSendersLast, RemoteCars.Pose pose, DateTime arrived)
    {
        // The first place starts the timeline rather than advancing it: there
        // is nothing before it for its gap to be a gap from.
        if (_arrivedLast is { } before)
        {
            _source += sinceTheSendersLast;

            double waited = (arrived - before).TotalMilliseconds;
            double ragged = Math.Abs(waited - sinceTheSendersLast);

            if (_measured)
            {
                _interval += (sinceTheSendersLast - _interval) * Follows;
                _jitter += (ragged - _jitter) * Follows;
            }
            else
            {
                _interval = sinceTheSendersLast;
                _jitter = ragged;
                _measured = true;
            }
        }

        _arrivedLast = arrived;
        _seen.Add((_source, pose));

        double keepFrom = _source - Remembered.TotalMilliseconds;
        while (_seen.Count > 2 && _seen[0].At < keepFrom) _seen.RemoveAt(0);
    }

    /// <summary>
    /// Where to draw the car now, or null while nothing has been heard.
    ///
    /// The point being drawn moves with this machine's own clock, so the car
    /// keeps going between arrivals instead of waiting for them, and is pulled
    /// back towards the right distance behind the newest place rather than
    /// snapped there - a correction that snaps is the jumping this exists to
    /// remove, in a smaller size.
    /// </summary>
    public RemoteCars.Pose? At(DateTime now)
    {
        if (_seen.Count == 0) return null;

        double want = _source - Delay.TotalMilliseconds;

        if (_drawnWhen is { } last)
        {
            _drawnAt += (now - last).TotalMilliseconds;

            if (StillArriving(now))
            {
                // Far out of step - a race just started, or the connection
                // stopped for a second - and creeping back would take longer
                // than anybody would watch a car sit still for.
                if (Math.Abs(want - _drawnAt) > Most.TotalMilliseconds) _drawnAt = want;
                else _drawnAt += (want - _drawnAt) * Follows;
            }
            else
            {
                // Nothing is coming. Held back towards a distance behind a
                // newest place that is no longer moving, the car would stop
                // short of where it was last seen and stay there - a car
                // parked forty milliseconds shy of its own last position, for
                // the rest of the race. So it runs out the places it has and
                // holds at the end of them.
                _drawnAt = Math.Min(_drawnAt, _source);
            }
        }
        else
        {
            _drawnAt = want;
        }

        _drawnWhen = now;

        return Between(_drawnAt);
    }

    /// <summary>
    /// Whether places are still coming.
    ///
    /// Three of the sender's own intervals, so an ordinary late one does not
    /// count as silence, with a floor for a sender whose interval has not been
    /// measured yet or is very short.
    /// </summary>
    bool StillArriving(DateTime now) =>
        _arrivedLast is { } last
        && (now - last).TotalMilliseconds <= Math.Max(3 * _interval, 100);

    /// <summary>
    /// The pose at a moment on the sender's timeline: between the two places
    /// that straddle it, or the nearest one when it falls outside what is
    /// held.
    /// </summary>
    RemoteCars.Pose Between(double at)
    {
        if (at <= _seen[0].At) return _seen[0].Pose;

        for (int i = 1; i < _seen.Count; i++)
        {
            if (_seen[i].At < at) continue;

            var (fromAt, from) = _seen[i - 1];
            var (toAt, to) = _seen[i];

            double span = toAt - fromAt;
            double part = span <= 0 ? 1 : (at - fromAt) / span;

            return Mix(from, to, part);
        }

        // Past everything held: the buffer has run dry, which is a place that
        // did not arrive. Holding the newest is what the game did before any
        // of this and is the honest answer - it is where the car was last
        // known to be.
        return _seen[^1].Pose;
    }

    static RemoteCars.Pose Mix(RemoteCars.Pose from, RemoteCars.Pose to, double part) =>
        new(
            new RemoteCars.Place(
                Straight(from.Place.X, to.Place.X, part),
                Straight(from.Place.Z, to.Place.Z, part),
                Straight(from.Place.Y, to.Place.Y, part)),
            Around(from.AroundX, to.AroundX, part),
            Around(from.AroundY, to.AroundY, part),
            Around(from.AroundZ, to.AroundZ, part),
            new RemoteCars.Wheels(
                Around(from.Wheels.A, to.Wheels.A, part),
                Around(from.Wheels.B, to.Wheels.B, part),
                Around(from.Wheels.C, to.Wheels.C, part),
                Around(from.Wheels.D, to.Wheels.D, part)));

    static int Straight(int from, int to, double part) =>
        (int)Math.Round(from + (to - from) * part);

    /// <summary>
    /// An angle, the short way round.
    ///
    /// A car crossing the wrap goes from just under a whole turn to just over
    /// nothing, and read straight that is the car spinning all the way back
    /// through every heading it does not have. The difference is taken to the
    /// nearer half-turn instead.
    ///
    /// The result is left near where it started rather than folded into a
    /// canonical range: the game reads these into a sine table and does not
    /// care which turn they are on, while a value quietly rewritten when
    /// nothing needed interpolating would be this changing a car that was
    /// already right.
    /// </summary>
    static short Around(short from, short to, double part)
    {
        const int Turn = RemoteCars.WholeTurn;

        int difference = (((to - from) % Turn) + Turn + Turn / 2) % Turn - Turn / 2;
        return (short)(int)Math.Round(from + difference * part);
    }
}
