using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Keeps the other players' cars where their owners say they are.
///
/// A pose travels: where the car is, and the three angles it faces along.
///
/// The heading cost seven attempts, all of them aimed at the wrong words. The
/// rotation at +0x218 is not state, it is a result: the game rebuilds all six
/// of its words every frame, for every car, from three angles thirty bytes
/// earlier. Copying an owner's matrix wrote over a value about to be
/// recomputed; building one from a yaw wrote a shape the game does not mean,
/// and drew the car stretched. Neither could have worked, and reading
/// gt2_ovr1_race_step_every_car_then_rebuild_its_rotation says so in one line.
///
/// So the angles travel and the matrix does not. A car that is sliding points
/// one way and moves another, and only its owner knows which - but the owner
/// need only send the same three shorts the game itself steers by.
///
/// What travels is a seat among the drivers, not a slot in the race. Every
/// machine rotates the driver it is built around to entrant 0 - the human
/// drives that one and the view follows it - so the same car is a different
/// slot on every machine, while the room's own ordering is the thing they all
/// already agree on. A viewer is built around the driver it is watching, and
/// so applies every seat rather than every seat but one.
///
/// On by default, because a multiplayer race without it is several people
/// driving alone on the same track. GT2_LONE_CARS turns it off, which is what
/// tells apart a fault in the sync from a fault under it.
/// </summary>
public static class CarSync
{
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("GT2_LONE_CARS") is (null or "");

    static int _sent;
    static int _applied;
    static bool _said;

    /// <summary>How often to say something, in frames, so a race is not narrated.</summary>
    const int Occasionally = 300;

    /// <summary>Called once per race frame, before the frame's work.</summary>
    public static void FrameBegins(IMemory m)
    {
        if (!Enabled) return;
        if (ModeHook.Wire is not { } wire) return;
        if (DirectRace.Racing is not { } race) return;

        // A viewer has no car, so it has no seat and nothing to send - and it
        // has to apply every seat, its leader's included, because the car at
        // slot 0 belongs to somebody else. A driver skips its own seat for the
        // opposite reason: slot 0 is the one it is actually steering.
        // MyName rather than Me: the two are the same player in every ordinary
        // race, and different ones when the game is driving this machine's car,
        // because then the grid is led by somebody else on purpose.
        int seat = race.Watching ? -1 : Seats.Of(race.Players, race.MyName);
        if (!race.Watching && seat < 0) return;

        int mine = race.MySlot;

        SayWhereTheGridPutUs(m, seat);

        if (seat >= 0)
        {
            var sending = RemoteCars.ReadPose(m, mine);
            wire.SendPlace((byte)seat, sending, ModeHook.HostToAnswer);
            _sent++;
            _lastSent = sending.Place;
        }

        wire.CollectPlaces();
        foreach (var (theirSeat, pose) in wire.Places)
        {
            if (theirSeat == seat) continue;
            if (theirSeat >= race.Players.Count) continue;

            int slot = RaceGrid.SlotFor(race.Players, race.Me, race.Players[theirSeat].Name);
            if (slot < 0) continue;

            // Every slot but this machine's own, whichever that is. A viewer
            // has none and applies them all.
            if (slot == mine && !race.Watching) continue;

            // Where the car is drawn, which is a little way behind where it
            // was last heard to be - see RemoteTrack. Falling back to the
            // newest place keeps this honest for the first frames of a race,
            // before there are two of them to draw between.
            var drawn = wire.PoseFor(theirSeat, DateTime.UtcNow) ?? pose;

            // Before the frame, on purpose: the rebuild that turns these three
            // angles into the matrix the car is drawn from runs later in it.
            RemoteCars.WritePose(m, slot, drawn);

            // And the wheels after it, for the mirror image of that reason -
            // see WheelsAreBeingDrawn.
            _wheels[slot] = drawn.Wheels;
            _applied++;
            _lastApplied = drawn.Place;

            // Watched on what arrived rather than on what was drawn. A car
            // whose places have stopped coming goes on creeping for as long as
            // the buffer lasts, and a watch on the drawn pose would call that
            // movement and say nothing.
            WatchForAStop(theirSeat, race.Players[theirSeat].Name, pose.Place);
            WatchForSilence(wire, theirSeat, race.Players[theirSeat].Name);
        }

        Say(wire, race);
    }

    static bool _stood;

    /// <summary>
    /// Whether to move this machine's own car onto another slot's square.
    ///
    /// It had to, once. Every machine rotates its own player to entrant 0, and
    /// while the grid was numbered by slot that put every machine's driven car
    /// on square zero - four players all started in one place. Since the number
    /// at +0x8D became the room's seat rather than the slot, entrant 0 already
    /// carries the number of the seat it belongs to, and moving it again takes
    /// it off its own square and onto the one it read. That is how the
    /// collision came back the moment each machine led the grid with its own
    /// player, and it is why this is off.
    ///
    /// Off, and staying off: four machines then reported the same square for
    /// the same number at +0x8D - place 0 at (713339, 358892) through place 3
    /// at (636879, 291710) - in four different rotations. The number stands the
    /// car; the slot does not. There is nothing left for this to correct.
    /// </summary>
    static readonly bool Stand =
        Environment.GetEnvironmentVariable("GT2_STAND_ON_SQUARE") is not (null or "");

    /// <summary>
    /// Says where the game stood every car, once, before anything has moved
    /// one - and moves this machine's own car only when asked to.
    ///
    /// Four machines' reports held side by side answered the question outright:
    /// the same square for the same number at +0x8D, each machine's list being
    /// that set in its own rotation. Kept because it costs one line a race and
    /// is the first thing to read when a car turns up somewhere unexpected.
    /// </summary>
    static void SayWhereTheGridPutUs(IMemory m, int seat)
    {
        if (_stood) return;
        _stood = true;

        var said = new System.Text.StringBuilder();
        for (int slot = 0; slot < RaceGrid.Slots; slot++)
        {
            var pose = RemoteCars.ReadPose(m, slot);
            said.Append($"{Environment.NewLine}[sync]   slot {slot}"
                + $" place {RaceGrid.PlaceOf(m, slot)}"
                + $" at ({pose.Place.X}, {pose.Place.Z}) facing {pose.AroundY}");
        }

        Console.Error.WriteLine($"[sync] seat {seat} finds the grid standing:" + said);

        // A viewer has no car to move, and seat zero would be moving onto its
        // own square.
        if (!Stand || seat <= 0) return;

        var square = RemoteCars.ReadPose(m, seat);
        RemoteCars.WritePose(m, 0, square);

        Console.Error.WriteLine(
            $"[sync] moved onto seat {seat}'s square at ({square.Place.X}, {square.Place.Z})");
    }

    /// <summary>
    /// What each slot's owner has their wheels turned to, as of the last place
    /// heard from them, or null for a slot nobody has spoken for.
    /// </summary>
    static readonly RemoteCars.Wheels?[] _wheels = new RemoteCars.Wheels?[RaceGrid.Slots];

    /// <summary>
    /// Post-hook on gt2_ovr1_race_car_build_the_matrices_it_is_drawn_from,
    /// which takes the car in A0 and is where a wheel's angle is worked out.
    ///
    /// It has to be here and it has to be after. A wheel's angle is derived
    /// every frame from the car's own physics, and a remote car has no physics
    /// worth the name - this port teleports it and tells it nothing - so what
    /// the game works out for one is wrong however early it is corrected.
    /// Writing after the function that derives it is the only moment the write
    /// survives to be drawn.
    ///
    /// The car is named by the pointer the game passes rather than by anything
    /// this port counts, which is how the array's own base was found in the
    /// first place.
    /// </summary>
    public static void WheelsAreBeingDrawn(CpuContext c, IMemory m)
    {
        if (!Enabled) return;
        if (DirectRace.Racing is null) return;

        long from = (long)c.A0 - RemoteCars.FirstCarObject;
        if (from < 0 || from % RemoteCars.CarStride != 0) return;

        long slot = from / RemoteCars.CarStride;
        if (slot < 0 || slot >= _wheels.Length) return;
        if (_wheels[slot] is not { } wheels) return;

        RemoteCars.WriteWheels(m, (int)slot, wheels);
    }

    /// <summary>
    /// Forgets the race just run, so the next one reports its own grid and
    /// counts its own places.
    /// </summary>
    public static void Forget()
    {
        _sent = 0;
        _applied = 0;
        _said = false;
        _stood = false;
        _lastSent = null;
        _lastApplied = null;
        _movement.Clear();
        _stopped.Clear();
        _silence.Clear();
        Array.Clear(_wheels);
    }

    /// <summary>
    /// The last position put on the wire and the last one taken off it.
    ///
    /// The counts alone cannot answer the question a frozen car asks. Places
    /// arriving and being applied is exactly what the log said while neither
    /// machine's car moved on the other's screen, because "applied" counts the
    /// write and not what was written: a sender stuck on one position and a
    /// receiver whose writes are being overwritten look identical from here.
    /// Two coordinates tell them apart at a glance - if the sent pair moves
    /// and the applied pair does not, the wire is at fault; if neither moves,
    /// the sender is reading a car that is not the one being driven; if both
    /// move, the write is landing somewhere the screen is not drawn from.
    /// </summary>
    static RemoteCars.Place? _lastSent;

    static RemoteCars.Place? _lastApplied;

    /// <summary>Where each seat last was, and when it last actually moved.</summary>
    static readonly Dictionary<int, (RemoteCars.Place Where, DateTime Moved)> _movement = [];

    /// <summary>Seats already reported as stopped, so it is said once.</summary>
    static readonly HashSet<int> _stopped = [];

    /// <summary>
    /// How long a car has to sit at one position before it is worth saying so.
    ///
    /// A car being driven never repeats a position for this long, and one
    /// whose places have stopped arriving repeats it forever - the last one
    /// that got through. Two seconds tells them apart without saying anything
    /// about a car that is merely stationary at a corner, which is a car
    /// standing still with new places still arriving and so still moving as
    /// far as this is concerned.
    /// </summary>
    static readonly TimeSpan LongEnoughToBeStuck = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Says when a car stopped, and when it started again.
    ///
    /// The counts could not answer this: places arriving and being applied is
    /// what the log said while both cars sat frozen on the other's screen,
    /// because "applied" counts the write and not whether what was written had
    /// changed. This watches the value.
    /// </summary>
    static void WatchForAStop(int seat, string who, RemoteCars.Place where)
    {
        var now = DateTime.UtcNow;

        if (!_movement.TryGetValue(seat, out var was))
        {
            _movement[seat] = (where, now);
            return;
        }

        if (was.Where != where)
        {
            _movement[seat] = (where, now);
            if (_stopped.Remove(seat))
                Console.Error.WriteLine(
                    $"[sync] {now:HH:mm:ss.fff} seat {seat} ({who}) is moving again"
                    + $" at ({where.X}, {where.Z})");
            return;
        }

        if (now - was.Moved < LongEnoughToBeStuck) return;
        if (!_stopped.Add(seat)) return;

        Console.Error.WriteLine(
            $"[sync] {now:HH:mm:ss.fff} seat {seat} ({who}) has not moved for"
            + $" {(now - was.Moved).TotalSeconds:F1}s - stuck at ({where.X}, {where.Z})");
    }

    /// <summary>
    /// How long nothing arriving from a seat stops being ordinary. Places come
    /// thirty times a second, so half a second is fifteen of them: past any
    /// reordering or late arrival, and short enough to catch an outage while
    /// it is still happening rather than after it.
    /// </summary>
    static readonly TimeSpan LongEnoughToBeSilence = TimeSpan.FromMilliseconds(500);

    /// <summary>When a seat went quiet, and what had been counted by then.</summary>
    static readonly Dictionary<int, (DateTime Began, int Missed, int Kept)> _silence = [];

    /// <summary>
    /// Says when a seat stops being heard from at all, and what the gap cost.
    ///
    /// This is the half <see cref="WatchForAStop"/> cannot tell you. A car that
    /// stops moving has two quite different causes - the other player is
    /// standing still, or nothing is reaching this machine - and on screen they
    /// are the same picture. The counter on the wire settles it: places lost
    /// during the quiet means the datagrams went missing, and none lost means
    /// they were merely late and arrived in a heap.
    ///
    /// Six seconds of a race went that way over a tunnel, and separating the
    /// two took reading how far the car had jumped when it came back. That is
    /// a guess. This is a count.
    /// </summary>
    static void WatchForSilence(LanSession wire, int seat, string who)
    {
        var now = DateTime.UtcNow;
        if (wire.SilenceFrom((byte)seat, now) is not { } quiet) return;

        int missed = wire.MissedFrom((byte)seat);
        int kept = wire.KeptFrom((byte)seat);

        if (quiet >= LongEnoughToBeSilence)
        {
            if (_silence.ContainsKey(seat)) return;

            _silence[seat] = (now - quiet, missed, kept);
            Console.Error.WriteLine(
                $"[sync] {now:HH:mm:ss.fff} nothing from seat {seat} ({who}) for"
                + $" {quiet.TotalSeconds:F1}s - {kept} place(s) kept and {missed} missed so far");
            return;
        }

        if (!_silence.Remove(seat, out var was)) return;

        Console.Error.WriteLine(
            $"[sync] {now:HH:mm:ss.fff} seat {seat} ({who}) is heard again after"
            + $" {(now - was.Began).TotalSeconds:F1}s - {missed - was.Missed} place(s) lost"
            + $" in it, {kept - was.Kept} arrived");
    }

    static string Where(RemoteCars.Place? place) =>
        place is null ? "nowhere yet" : $"({place.X}, {place.Z})";

    static void Say(LanSession wire, DirectRace.Pending race)
    {
        if (_said && _sent % Occasionally != 0) return;
        _said = true;

        Console.Error.WriteLine(
            $"[sync] {_sent} place(s) sent, {_applied} applied,"
            + $" {wire.Places.Count} seat(s) heard from, {race.Players.Count} driving"
            + (race.Watching ? $", watching {race.Me}" : "")
            + $" - this car at {Where(_lastSent)},"
            + $" last one applied {Where(_lastApplied)}");

        // One line per car, named, because "the opponents stopped" is a
        // question about a particular car and the totals are about all of
        // them.
        var now = DateTime.UtcNow;
        foreach (var (theirSeat, pose) in wire.Places)
        {
            string who = theirSeat < race.Players.Count
                ? race.Players[theirSeat].Name
                : "somebody not in the room";

            string moved = _movement.TryGetValue(theirSeat, out var seen)
                ? $"{(now - seen.Moved).TotalSeconds:F1}s ago"
                : "never";

            Console.Error.WriteLine(
                $"[sync]   seat {theirSeat} ({who}) at ({pose.Place.X}, {pose.Place.Z})"
                + $" - last moved {moved},"
                + $" drawn {wire.DelayFor(theirSeat).TotalMilliseconds:F0}ms behind");
        }
    }
}
