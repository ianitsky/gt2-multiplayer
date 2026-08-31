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
/// Off unless GT2_CAR_SYNC is set.
/// </summary>
public static class CarSync
{
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("GT2_CAR_SYNC") is not (null or "");

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
        int seat = race.Watching ? -1 : Seats.Of(race.Players, race.Me);
        if (!race.Watching && seat < 0) return;

        SayWhereTheGridPutUs(m, seat);

        if (seat >= 0)
        {
            wire.SendPlace((byte)seat, RemoteCars.ReadPose(m, 0), ModeHook.HostToAnswer);
            _sent++;
        }

        wire.CollectPlaces();
        foreach (var (theirSeat, pose) in wire.Places)
        {
            if (theirSeat == seat) continue;
            if (theirSeat >= race.Players.Count) continue;

            int slot = RaceGrid.SlotFor(race.Players, race.Me, race.Players[theirSeat].Name);
            if (slot < 0) continue;
            if (slot == 0 && !race.Watching) continue;

            // Before the frame, on purpose: the rebuild that turns these three
            // angles into the matrix the car is drawn from runs later in it.
            RemoteCars.WritePose(m, slot, pose);
            _applied++;
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
    /// Kept as a switch rather than deleted because the report below is what
    /// says which of the two the game actually stands a car by.
    /// </summary>
    static readonly bool Stand =
        Environment.GetEnvironmentVariable("GT2_STAND_ON_SQUARE") is not (null or "");

    /// <summary>
    /// Says where the game stood every car, once, before anything has moved
    /// one - and moves this machine's own car only when asked to.
    ///
    /// Two machines' reports held side by side answer the question outright.
    /// If a car is stood by its entrant index, slot i is the same square on
    /// every machine and the two lists come out identical. If it is stood by
    /// the place written at +0x8D, each machine's list is the same squares in
    /// its own rotation - and then a machine's own car is already standing
    /// exactly where the room says it should.
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

    static void Say(LanSession wire, DirectRace.Pending race)
    {
        if (_said && _sent % Occasionally != 0) return;
        _said = true;

        Console.Error.WriteLine(
            $"[sync] {_sent} place(s) sent, {_applied} applied,"
            + $" {wire.Places.Count} seat(s) heard from, {race.Players.Count} driving"
            + (race.Watching ? $", watching {race.Me}" : ""));
    }
}
