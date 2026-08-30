using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Puts a car where something other than the game says it should be.
///
/// This is the premise the whole state-sync plan rests on, and it is worth
/// proving before a byte of protocol is written: if the game's own physics
/// fights a position written from outside - snapping back, jittering,
/// launching the car off the track - then sending positions over a wire will
/// not work either, and that is better learned now.
///
/// So the first mode has no network in it at all. GT2_GHOST makes the second
/// car shadow the first at a fixed offset: drive, and if it drives with you,
/// writing a car's place works.
///
/// Where to write was deduced rather than recognised. A read watch on the
/// entrant at 0x801D58B8 named entry_80028DDC, which builds the cars; the
/// motion lives in the array at 0x800A9B04 every 0xB40, and six cars racing
/// gave the position offsets - X, Z and Y, with a second copy 0x24 on that is
/// written too, since the game keeps both.
/// </summary>
public static class RemoteCars
{
    /// <summary>The array of what each car is doing, and how far apart they are.</summary>
    public const uint FirstCar = 0x800A9B04u;
    public const int CarStride = 0xB40;
    public const int Cars = 6;

    /// <summary>Where a car keeps its place, and where it keeps it again.</summary>
    public const uint X = 0x20Cu;
    public const uint Z = 0x210u;
    public const uint Y = 0x214u;
    public const uint SecondCopy = 0x24u;

    /// <summary>
    /// Whether to make car one shadow car zero. Off unless GT2_GHOST is set;
    /// it is a proof, not a feature.
    /// </summary>
    static readonly bool Ghosting =
        Environment.GetEnvironmentVariable("GT2_GHOST") is not (null or "");

    /// <summary>
    /// How far to one side to put the ghost, in the game's fixed point.
    ///
    /// Far enough to see it is not the player's own car, near enough to stay
    /// on a track: the six cars in a measured race sat within about a hundred
    /// thousand of each other.
    /// </summary>
    const int Beside = 60_000;

    static bool _said;

    public sealed record Place(int X, int Z, int Y);

    /// <summary>Where a car is now.</summary>
    public static Place Read(IMemory m, int car)
    {
        uint at = FirstCar + (uint)(car * CarStride);
        return new Place((int)m.ReadU32(at + X), (int)m.ReadU32(at + Z), (int)m.ReadU32(at + Y));
    }

    /// <summary>
    /// Puts a car somewhere, both copies.
    ///
    /// Both, because the game keeps the position twice and writing one would
    /// leave whichever reads the other disagreeing about where the car is.
    /// </summary>
    public static void Write(IMemory m, int car, Place place)
    {
        uint at = FirstCar + (uint)(car * CarStride);
        foreach (uint copy in new[] { 0u, SecondCopy })
        {
            m.WriteU32(at + X + copy, (uint)place.X);
            m.WriteU32(at + Z + copy, (uint)place.Z);
            m.WriteU32(at + Y + copy, (uint)place.Y);
        }
    }

    /// <summary>Called once per race frame.</summary>
    public static void FrameBegins(IMemory m)
    {
        if (!Ghosting) return;

        var mine = Read(m, 0);
        Write(m, 1, mine with { Z = mine.Z + Beside });

        if (_said) return;
        _said = true;
        Console.Error.WriteLine(
            $"[ghost] car 1 is following car 0, {Beside} to one side"
            + $" (car 0 is at {mine.X}, {mine.Z}, {mine.Y})");
    }
}
