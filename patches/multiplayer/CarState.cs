using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Prints the six cars so their fields can be read for what they mean.
///
/// Where they are was deduced, not recognised. Five attempts to spot a car by
/// the shape of its numbers found display lists three times, a stray 4096 once
/// and a decompression buffer once. So instead a read watch was pointed at the
/// entrant at 0x801D58B8 - a fixed address whatever the heap does - and it
/// named entry_80028DDC, which walks the entrants during the race screen's
/// first pass and builds a car from each:
///
///     T0 = 0x801D585C                    the race block
///     S1 = [SP+0x44] + T0                the entrant, from +0x5C, stride 0xD0
///     A0 = [S1]                          its car id
///     S4 = [SP+0x40] + 0x000B6394 + 0x800A9500
///     [screen] = S4                      the live car
///
/// and at the foot of the loop [SP+0x44] += 0xD0, [SP+0x40] += 0x5000, and the
/// pointer array walks on by four. So the cars are 0x5000 apart from
/// 0x8015F894, and that comes from the game's own arithmetic rather than from
/// a pattern that happened to hold in one race.
/// </summary>
public static class CarState
{
    static readonly bool Watching =
        Environment.GetEnvironmentVariable("GT2_CAR_STATE") is not (null or "");

    /// <summary>Where car zero's object begins, and how far to the next.</summary>
    public const uint FirstCar = 0x8015F894u;
    public const int CarStride = 0x5000;
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
