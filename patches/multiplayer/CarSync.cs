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
            wire.SendPlace((byte)seat, RemoteCars.ReadPose(m, mine), ModeHook.HostToAnswer);
            _sent++;
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

            // Before the frame, on purpose: the rebuild that turns these three
            // angles into the matrix the car is drawn from runs later in it.
            RemoteCars.WritePose(m, slot, pose);

            // And the wheels after it, for the mirror image of that reason -
            // see WheelsAreBeingDrawn.
            _wheels[slot] = pose.Wheels;
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
        Array.Clear(_wheels);
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
