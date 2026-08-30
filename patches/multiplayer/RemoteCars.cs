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
    /// So a transform is nine words:
    ///
    ///     +0x20C  X, Z, Y
    ///     +0x218  rotation, six words
    ///
    /// and the whole of it is kept twice, 0x24 apart - which is why 0x24 was
    /// already the right distance for the position alone.
    ///
    /// The rotation is not a thing to write. The game rebuilds all six of
    /// those words every frame, for every car, in
    /// gt2_ovr1_race_step_every_car_then_rebuild_its_rotation - which ends by
    /// calling gt2_ovr1_race_rotation_matrix_from_three_angles once per car
    /// with the three angles at <see cref="Heading"/>. Write the angles
    /// instead and the game builds the matrix itself.
    /// </summary>
    public const uint Transform = 0x20Cu;
    public const int TransformWords = 9;
    public const uint SecondCopy = 0x24u;

    /// <summary>Where the place sits inside a transform, for callers that only want it.</summary>
    public const uint X = 0x20Cu;
    public const uint Z = 0x210u;
    public const uint Y = 0x214u;

    /// <summary>
    /// Which way a car faces: three angles, sixteen bits each, 4096 to a turn.
    ///
    /// These are the source and the rotation at +0x218 is the derivative. The
    /// game builds that matrix as
    ///
    ///     row0 = ( cz*sy*sx - sz*cx ,  sz*sy*sx + cz*cx ,  cy*sx )
    ///     row1 = (      cz*cy       ,       sz*cy       ,   -sy  )
    ///     row2 = ( cz*sy*cx + sz*sx ,  sz*sy*cx - cz*sx ,  cy*cx )
    ///
    /// where s and c are sine and cosine of the angle about each axis, taken
    /// from one table at 0x80093150 in which cosine is the same table read a
    /// quarter turn along. Putting the numbers a real car read into that -
    /// x about half a degree, y about two, z a quarter turn shy of nothing -
    /// gives back the nine values above to within three parts in four
    /// thousand, which is what settles that these are the right three shorts.
    ///
    /// The third is the heading. Car zero read it near a negative quarter turn
    /// while it drove down +X; the other two stayed within a couple of degrees
    /// of nothing, which is a car sitting flat on a road.
    /// </summary>
    public const uint Heading = 0x1F4u;
    public const int HeadingAngles = 3;

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
    /// <summary>
    /// Whether to copy the whole 0xB40 of a car rather than its transform.
    ///
    /// The transform sticks - both copies, every frame checked - and the car
    /// still does not turn, so the renderer reads its heading from somewhere
    /// other than +0x218. Guessing which offset that is has been wrong five
    /// times in this hunt, so this splits the question instead of answering
    /// it: copy everything, and if the ghost then mirrors the player's heading
    /// the answer is inside this structure and can be bisected. If it still
    /// does not turn, the heading is outside it and the search moves.
    ///
    /// Blunt on purpose. It may well copy a pointer that belongs to car zero
    /// and draw the wrong thing or fall over; that is a result too.
    /// </summary>
    static readonly bool Wholesale =
        Environment.GetEnvironmentVariable("GT2_GHOST_WHOLE") is not (null or "");

    static readonly bool Spinning =
        Environment.GetEnvironmentVariable("GT2_GHOST_SPIN") is not (null or "");

    /// <summary>A whole turn, in the units the game counts angles in.</summary>
    public const int WholeTurn = 4096;

    /// <summary>
    /// How far to turn the spinning ghost each frame, in the game's own units.
    ///
    /// Eight of them is about half a turn a second, fast enough that a steady
    /// spin and a stuttering one look alike. GT2_GHOST_SPIN_RATE moves it.
    /// </summary>
    static readonly int PerFrame =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_GHOST_SPIN_RATE"), out int r) ? r : 8;

    static int _turned;

    static bool _said;
    static Pose? _wrote;

    /// <summary>
    /// Whether what was written last frame is still there this frame.
    ///
    /// The matrix at +0x218 was watched this way first, and every reading of
    /// it was ambiguous: a word that comes back changed has been recomputed,
    /// and a word that comes back whole may only mean the recompute agreed.
    /// The angles have no such trouble. They are the input, so a frame that
    /// gives them back unchanged is a frame the game accepted them for.
    /// </summary>
    static void SayWhatStuck(IMemory m)
    {
        if (_wrote is null || _checks >= MostChecks) return;

        var now = ReadPose(m, 1);
        if (now == _wrote)
        {
            if (_clean++ == 0)
                Console.Error.WriteLine("[ghost] the pose written last frame is still there");
            return;
        }

        _checks++;
        Console.Error.WriteLine(
            $"[ghost] frame {_frames}: written {_wrote}, now {now}");
    }

    /// <summary>How many frames of disagreement to report before stopping.</summary>
    const int MostChecks = 6;

    static int _checks;
    static int _clean;
    static int _frames;

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

    /// <summary>
    /// Where a car is and which way it faces - everything one machine has to
    /// tell another about a car, and nothing that either can work out alone.
    ///
    /// The angles are named for the axis each turns about, because that is all
    /// the game says about them. AroundZ is the heading; the other two are a
    /// car's lean on the road and stay within a couple of degrees of nothing.
    /// </summary>
    public sealed record Pose(Place Place, short AroundX, short AroundY, short AroundZ);

    /// <summary>Which way a car faces, as the three angles the game rebuilds from.</summary>
    public static Pose ReadPose(IMemory m, int car)
    {
        uint at = FirstCar + (uint)(car * CarStride) + Heading;
        return new Pose(
            Read(m, car),
            (short)m.ReadU16(at),
            (short)m.ReadU16(at + 2u),
            (short)m.ReadU16(at + 4u));
    }

    /// <summary>
    /// Puts a car somewhere, facing a given way.
    ///
    /// Only the place and the angles are written. The rotation at +0x218 is
    /// left alone on purpose: the game rebuilds it from these three angles
    /// later in the same frame, so writing it as well would be writing over
    /// the answer with the question.
    /// </summary>
    public static void WritePose(IMemory m, int car, Pose pose)
    {
        Write(m, car, pose.Place);

        uint at = FirstCar + (uint)(car * CarStride) + Heading;
        m.WriteU16(at, (ushort)pose.AroundX);
        m.WriteU16(at + 2u, (ushort)pose.AroundY);
        m.WriteU16(at + 4u, (ushort)pose.AroundZ);
    }

    /// <summary>
    /// Whether to write after the frame's work rather than before it.
    ///
    /// This was how the matrix at +0x218 was chased. Written before the
    /// frame, three of its six words came back changed by about five units in
    /// four thousand; written with a heading of the ghost's own, all six came
    /// back. Both readings were of the same thing - the game rebuilding that
    /// matrix from the angles, later in the frame - and moving the write after
    /// it only won the race rather than settling the argument.
    ///
    /// The angles are the argument's end, and they do not care: they are read
    /// during the frame, so a write before it is the write that counts. Kept
    /// because a place written late still lands, and comparing the two moments
    /// costs nothing.
    ///
    /// On by default; GT2_GHOST_EARLY puts the old moment back for comparison.
    /// </summary>
    static readonly bool AfterTheFrame =
        Environment.GetEnvironmentVariable("GT2_GHOST_EARLY") is (null or "");

    /// <summary>Called after the frame's work.</summary>
    public static void FrameEnds(IMemory m)
    {
        if (Ghosting && AfterTheFrame) Put(m);
    }

    /// <summary>Called once per race frame, before the frame's work.</summary>
    public static void FrameBegins(IMemory m)
    {
        if (!Ghosting) return;

        _frames++;
        SayWhatStuck(m);
        if (!AfterTheFrame) Put(m);
    }

    /// <summary>Puts the player's pose on the ghost, offset to one side.</summary>
    static void Put(IMemory m)
    {
        if (Wholesale)
        {
            uint from = FirstCar;
            uint to = FirstCar + CarStride;
            for (uint i = 0; i < CarStride; i += 4) m.WriteU32(to + i, m.ReadU32(from + i));
        }

        var mine = ReadPose(m, 0);
        mine = mine with { Place = mine.Place with { Z = mine.Place.Z + Beside } };

        if (Spinning)
        {
            _turned = (_turned + PerFrame) % WholeTurn;
            mine = mine with { AroundZ = (short)_turned };
        }

        WritePose(m, 1, mine);
        _wrote = mine;

        if (_said) return;
        _said = true;
        var place = mine.Place;
        Console.Error.WriteLine(
            $"[ghost] car 1 is following car 0, {Beside} to one side"
            + (Wholesale ? $", the whole 0x{CarStride:X} of it" : "")
            + $" (car 0 faces {mine.AroundX}, {mine.AroundY}, {mine.AroundZ}"
            + $" and sits at {place.X}, {place.Z}, {place.Y})");
    }
}
