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
    /// The contact points around a car sit about eighteen thousand apart, so
    /// that is roughly a car's width. Sixty thousand was three of them and put
    /// the ghost too far away to watch - and a thing you cannot see is a thing
    /// you cannot tell is working. GT2_GHOST_BESIDE moves it.
    /// </summary>
    static readonly int Beside =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_GHOST_BESIDE"), out int b) ? b : 22_000;

    /// <summary>
    /// Whether to spin the ghost instead of copying the player's heading.
    ///
    /// Copying it makes the ghost face roughly where the player faces, which
    /// is exactly the thing that is hard to judge from a replay - and the
    /// readback says the write survives the frame, so what is in doubt is not
    /// whether the words stick but whether these are the words the car is
    /// drawn from. A ghost turning steadily on its own while the player drives
    /// straight settles that in a second, without anyone having to compare two
    /// headings by eye.
    /// </summary>
    static readonly bool Spinning =
        Environment.GetEnvironmentVariable("GT2_GHOST_SPIN") is not (null or "");

    /// <summary>One in the game's twelve-bit fixed point.</summary>
    const int One = 4096;

    /// <summary>How far to turn the spinning ghost each frame, in degrees.</summary>
    const double PerFrame = 3.0;

    static double _turned;

    /// <summary>
    /// A yaw as the game stores one: a 3x3 of sixteen-bit values in rows of
    /// four shorts, so the diagonal lands on shorts 0, 5 and 10. The middle
    /// term is negative because the game counts Y downwards, which is how a
    /// car going straight reads -4092 there rather than 4092.
    /// </summary>
    internal static uint[] YawFor(double degrees) => Yaw(degrees);

    static uint[] Yaw(double degrees)
    {
        double r = degrees * Math.PI / 180.0;
        short cos = (short)Math.Round(Math.Cos(r) * One);
        short sin = (short)Math.Round(Math.Sin(r) * One);

        short[] m =
        [
            cos, 0, sin, 0,
            0, (short)-One, 0, 0,
            (short)-sin, 0, cos, 0,
        ];

        var words = new uint[6];
        for (int i = 0; i < words.Length; i++)
            words[i] = (uint)((ushort)m[i * 2] | ((uint)(ushort)m[i * 2 + 1] << 16));
        return words;
    }

    static bool _said;
    static uint[]? _wrote;
    static bool _checked;

    /// <summary>
    /// Whether what was written last frame is still there this frame.
    ///
    /// Watching a ghost and judging whether it turns is a hard thing to do
    /// from a replay, and an easy thing for the program to answer: write nine
    /// words, come back a frame later, and see which of them the game has put
    /// back. Whatever it overwrites, it recomputes - and recomputed state
    /// cannot be driven from a wire.
    /// </summary>
    static void SayWhatStuck(IMemory m)
    {
        if (_checked || _wrote is null) return;
        _checked = true;

        var now = ReadTransform(m, 1);
        var lost = new List<string>();
        for (int i = 0; i < now.Length; i++)
            if (now[i] != _wrote[i])
                lost.Add($"+0x{Transform + (uint)(i * 4):X3} written {(int)_wrote[i]} now {(int)now[i]}");

        Console.Error.WriteLine(lost.Count == 0
            ? "[ghost] a frame later the whole transform is still as written"
            : $"[ghost] a frame later {lost.Count} of {now.Length} words were put back:");
        foreach (string one in lost) Console.Error.WriteLine($"[ghost]   {one}");
    }

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
        SayWhatStuck(m);

        var mine = ReadTransform(m, 0);
        mine[1] = unchecked((uint)((int)mine[1] + Beside));

        if (Spinning)
        {
            _turned += PerFrame;
            var yaw = Yaw(_turned);
            for (int i = 0; i < yaw.Length; i++) mine[3 + i] = yaw[i];
        }

        WriteTransform(m, 1, mine);
        _wrote = mine;

        if (_said) return;
        _said = true;
        var place = Read(m, 0);
        Console.Error.WriteLine(
            $"[ghost] car 1 is following car 0 whole, {Beside} to one side"
            + $" (car 0 is at {place.X}, {place.Z}, {place.Y})");
    }
}
