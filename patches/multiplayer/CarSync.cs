using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Keeps the other players' cars where their owners say they are.
///
/// Only the place travels, and that is a decision rather than an omission. A
/// car's heading at +0x218 is derived: the physics rebuilds it every frame
/// from the car's own state, before the display list that draws it is built,
/// so a heading written from outside is either undone or arrives too late. A
/// car moved along its owner's path orients itself to that path, which is why
/// the first ghost "followed the direction and the speed" while facing its own
/// way. Approximately right for free beats exactly right never.
///
/// What travels is a seat in the room, not a slot in the race. Every machine
/// rotates its own player to entrant 0 - the human always drives that one - so
/// the same car is a different slot on every machine, while the room's own
/// ordering is the thing they all already agree on.
///
/// Off unless GT2_CAR_SYNC is set.
/// </summary>
public static class CarSync
{
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("GT2_CAR_SYNC") is not (null or "");

    /// <summary>
    /// Where each seat's car was last seen, so a heading can be worked out.
    ///
    /// A remote car is teleported along its owner's path, but its own physics
    /// keeps simulating an AI driving somewhere else, and the heading it draws
    /// with is that simulation's - which is why a car that moves correctly
    /// still faces the wrong way. Nothing is sent about heading and nothing
    /// needs to be: two places a frame apart say which way a car is going.
    /// </summary>
    static readonly Dictionary<byte, RemoteCars.Place> _wasAt = [];

    /// <summary>
    /// How far a car must have moved before its heading is worth recomputing.
    ///
    /// A car sitting still has no direction of travel, and atan2 of nothing is
    /// noise - a stationary car would spin on the spot.
    /// </summary>
    const int Moved = 200;

    /// <summary>
    /// The heading a car has when its rotation reads as the identity.
    ///
    /// Taken from the measurement rather than assumed: car zero's matrix was
    /// within a few units of the identity while it travelled 465787 along X
    /// and 2138 along Z, so a car pointing down +X is a rotation of nothing.
    /// The row that ends up as the world-space forward axis is (cos, 0, sin),
    /// which puts +Z at a quarter turn.
    /// </summary>
    static double Heading(RemoteCars.Place from, RemoteCars.Place to) =>
        Math.Atan2(to.Z - from.Z, to.X - from.X) * 180.0 / Math.PI;

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

        int seat = Seat(race, race.Me);
        if (seat < 0) return;

        var mine = RemoteCars.Read(m, 0);
        wire.SendPlace((byte)seat, mine.X, mine.Z, mine.Y, ModeHook.HostToAnswer);
        _sent++;

        wire.CollectPlaces();
        foreach (var (theirSeat, place) in wire.Places)
        {
            if (theirSeat == seat) continue;
            if (theirSeat >= race.Players.Count) continue;

            int slot = RaceGrid.SlotFor(race.Players, race.Me, race.Players[theirSeat].Name);
            if (slot <= 0) continue;

            var now = new RemoteCars.Place(place.X, place.Z, place.Y);
            PutThere(m, slot, theirSeat, now);
            _applied++;
        }

        Say(wire, race);
    }

    /// <summary>
    /// Puts a car where its owner says, facing the way it is going.
    ///
    /// Written before the frame's work rather than after it. After is where a
    /// rotation survives in memory, and it is also after whatever draws the
    /// car, so it survives to no purpose. Before, the physics rebuilds it -
    /// but it rebuilds it every frame anyway, and this is written every frame
    /// too, so the car is drawn from whichever won that frame. Position proved
    /// the point: written before, it draws.
    /// </summary>
    static void PutThere(IMemory m, int slot, byte seat, RemoteCars.Place now)
    {
        var words = RemoteCars.ReadTransform(m, slot);
        words[0] = unchecked((uint)now.X);
        words[1] = unchecked((uint)now.Z);
        words[2] = unchecked((uint)now.Y);

        if (_wasAt.TryGetValue(seat, out var before)
            && (Math.Abs(now.X - before.X) > Moved || Math.Abs(now.Z - before.Z) > Moved))
        {
            var yaw = RemoteCars.YawFor(Heading(before, now));
            for (int i = 0; i < yaw.Length; i++) words[3 + i] = yaw[i];
            _wasAt[seat] = now;
        }
        else if (!_wasAt.ContainsKey(seat))
        {
            _wasAt[seat] = now;
        }

        RemoteCars.WriteTransform(m, slot, words);
    }

    /// <summary>Which seat in the room a player holds, which is what travels.</summary>
    static int Seat(DirectRace.Pending race, string who)
    {
        for (int i = 0; i < race.Players.Count; i++)
            if (race.Players[i].Name == who) return i;
        return -1;
    }

    static void Say(LanSession wire, DirectRace.Pending race)
    {
        if (_said && _sent % Occasionally != 0) return;
        _said = true;

        Console.Error.WriteLine(
            $"[sync] {_sent} place(s) sent, {_applied} applied,"
            + $" {wire.Places.Count} seat(s) heard from, {race.Players.Count} in the room");
    }
}
