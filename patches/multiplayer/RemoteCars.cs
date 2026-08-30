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

    /// <summary>
    /// Where a car keeps its transform, and how big one is.
    ///
    /// The ghost proved position works and showed what it misses: a car
    /// following another kept its heading. The rotation sits directly after
    /// the place, as a 3x3 of sixteen-bit values with each row padded to eight
    /// bytes - the diagonal falls on shorts 0, 5 and 10, and a car going
    /// straight reads
    ///
    ///     [ 4095   -13     37 ]
    ///     [  -11  -4092  -151 ]
    ///     [  -37   -149   4093 ]
    ///
    /// which is very nearly the identity, with m22 negative because the game
    /// counts Y downwards. So a transform is nine words:
    ///
    ///     +0x20C  X, Z, Y
    ///     +0x218  rotation, six words
    ///
    /// and the whole of it is kept twice, 0x24 apart - which is why 0x24 was
    /// already the right distance for the position alone.
    /// </summary>
    public const uint Transform = 0x20Cu;
    public const int TransformWords = 9;
    public const uint SecondCopy = 0x24u;

    /// <summary>Where the place sits inside a transform, for callers that only want it.</summary>
    public const uint X = 0x20Cu;
    public const uint Z = 0x210u;
    public const uint Y = 0x214u;

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
    /// Both, because the game keeps the transform twice and writing one would
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

    /// <summary>A car's whole transform: where it is and which way it faces.</summary>
    public static uint[] ReadTransform(IMemory m, int car)
    {
        uint at = FirstCar + (uint)(car * CarStride) + Transform;
        var words = new uint[TransformWords];
        for (int i = 0; i < words.Length; i++) words[i] = m.ReadU32(at + (uint)(i * 4));
        return words;
    }

    /// <summary>Puts a whole transform on a car, in both copies.</summary>
    public static void WriteTransform(IMemory m, int car, uint[] words)
    {
        uint at = FirstCar + (uint)(car * CarStride) + Transform;
        foreach (uint copy in new[] { 0u, SecondCopy })
            for (int i = 0; i < words.Length && i < TransformWords; i++)
                m.WriteU32(at + copy + (uint)(i * 4), words[i]);
    }

    /// <summary>Called once per race frame.</summary>
    public static void FrameBegins(IMemory m)
    {
        if (!Ghosting) return;

        // The whole transform, not just the place. Copying the place alone
        // gave a car that went where the player went and kept facing whatever
        // way it had been pointing.
        var mine = ReadTransform(m, 0);
        mine[1] = unchecked((uint)((int)mine[1] + Beside));
        WriteTransform(m, 1, mine);

        if (_said) return;
        _said = true;
        var place = Read(m, 0);
        Console.Error.WriteLine(
            $"[ghost] car 1 is following car 0 whole, {Beside} to one side"
            + $" (car 0 is at {place.X}, {place.Z}, {place.Y})");
    }
}
