using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Keeps the other players' cars where their owners say they are.
///
/// A whole transform travels: where the car is and which way it faces.
///
/// The heading took two wrong turns to get right. It was left out at first, on
/// the belief that a rotation written from outside could not reach the
/// renderer - the physics rebuilds +0x218 every frame, and a readback taken a
/// frame later showed it rebuilt. That readback could not tell "never drawn"
/// from "drawn and then rebuilt", and the second is what was happening.
///
/// Then it was worked out from two places a frame apart, which turned every
/// remote car to face the way it was travelling. Close to right, and not
/// right: a car that is sliding points one way and moves another. The owner
/// knows exactly which way its car faces, so the owner sends it.
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
    /// Why the heading is not applied, though it is sent.
    ///
    /// The words at +0x218 pack as a 3x3 in rows of four shorts - that much is
    /// certain, since the matrix measured off a moving car is orthonormal that
    /// way and no other, with a determinant of minus one for a left-handed
    /// frame with Y counted downwards.
    ///
    /// What they are not is a car's orientation in the world. All six cars
    /// read within a few units of the identity at the same moment, on a track
    /// that curves; world orientations could not all be the identity at once.
    /// They are relative to something the port has not identified.
    ///
    /// That is why both attempts failed and failed differently. Copying the
    /// owner's matrix copies a value relative to the owner's frame, so the car
    /// faces wrongly. Building a world yaw writes something the game does not
    /// mean, so the car is drawn stretched and tilted - which is at least
    /// proof that these words reach the renderer.
    ///
    /// The place is not relative to anything and works, so the place is what
    /// is applied. The whole transform still travels, because the wire is not
    /// the problem and a later fix should not need a new message.
    /// </summary>
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

        wire.SendPlace((byte)seat, RemoteCars.ReadTransform(m, 0), ModeHook.HostToAnswer);
        _sent++;

        wire.CollectPlaces();
        foreach (var (theirSeat, place) in wire.Places)
        {
            if (theirSeat == seat) continue;
            if (theirSeat >= race.Players.Count) continue;

            int slot = RaceGrid.SlotFor(race.Players, race.Me, race.Players[theirSeat].Name);
            if (slot <= 0) continue;

            // The place only. Everything tried for the heading made it worse:
            // the words at +0x218 are not a car's orientation in the world.
            RemoteCars.Write(m, slot,
                new RemoteCars.Place((int)place[0], (int)place[1], (int)place[2]));
            _applied++;
        }

        Say(wire, race);
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
