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

            RemoteCars.WriteTransform(m, slot, place);
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
