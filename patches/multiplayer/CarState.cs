using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Prints the six cars so their fields can be read for what they mean.
///
/// The differ settled where they are. Six windows of 0xB40 laid against
/// 0x800A9B04 came back with 130, 139, 149, 127, 127 and 130 moving words -
/// six of a kind, which is what says the base and the stride are right:
///
///     car 0  0x800A9B04      car 3  0x800ABCC4
///     car 1  0x800AA644      car 4  0x800AC804
///     car 2  0x800AB184      car 5  0x800AD344
///
/// What it cannot settle is meaning. "This word moves" is true of a position,
/// a velocity, a wheel angle, an engine note and a lap timer alike. Values
/// tell them apart: a coordinate is large and drifts smoothly and differs
/// between cars by where they are on the track; a velocity swings through
/// zero; a heading wraps; a counter only ever climbs.
///
/// So this prints a window of every car at a few moments, and the reading is
/// done outside. Six cars beside each other is the point - a field that holds
/// six different values that all change is a per-car quantity, and a field
/// that holds the same value in all six is the track or the weather.
///
/// Off unless GT2_CAR_STATE is set.
/// </summary>
public static class CarState
{
    static readonly bool Watching =
        Environment.GetEnvironmentVariable("GT2_CAR_STATE") is not (null or "");

    /// <summary>Where car zero's object begins, and how far to the next.</summary>
    public const uint FirstCar = 0x800A9B04u;
    public const int CarStride = 0xB40;
    public const int Cars = 6;

    /// <summary>How much of each car to print. The moving offsets end by +0x400.</summary>
    static readonly int Window =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_CAR_WINDOW"), out int w) ? w : 0x400;

    /// <summary>How long to wait, how often to print, and how many times.</summary>
    static readonly int After =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_CAR_AFTER"), out int a) ? a : 420;

    const int Every = 120;
    const int Times = 3;

    static int _frames;
    static int _printed;

    /// <summary>Called once per race frame.</summary>
    public static void FrameBegins(IMemory m)
    {
        if (!Watching || _printed >= Times) return;
        if (_frames++ < After) return;
        if ((_frames - After) % Every != 0) return;

        _printed++;
        Console.Error.WriteLine($"[car] sample {_printed}, frame {_frames}:");
        for (int car = 0; car < Cars; car++) Say(m, car);
    }

    /// <summary>
    /// One car, as words, eight to a line so a wrapped console still reads.
    /// The offset leads each line, because the offset is the thing being
    /// hunted and counting along a wrapped line to find it is how mistakes
    /// get made.
    /// </summary>
    static void Say(IMemory m, int car)
    {
        uint at = FirstCar + (uint)(car * CarStride);

        for (int off = 0; off < Window; off += 32)
        {
            var line = new System.Text.StringBuilder();
            for (int i = 0; i < 32 && off + i < Window; i += 4)
                line.Append($" {(int)m.ReadU32(at + (uint)(off + i)),11}");

            Console.Error.WriteLine($"[car] {car} +0x{off:X3}{line}");
        }
    }
}
