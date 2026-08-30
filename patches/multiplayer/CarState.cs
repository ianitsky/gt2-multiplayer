using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Prints the six cars so their fields can be read for what they mean.
///
/// There are two per-car arrays and they were confused for each other twice.
///
/// entry_80028DDC builds one of them: it walks the entrants from 0x801D585C
/// and lays a car out every 0x5000 from 0x8015F894. Printing all six showed
/// them populated and all different - a pointer apiece, 0x888888 three times -
/// and the differ then found **not one moving word** in the whole 0x5000 of
/// any of them across a race being driven. That array is the car's model and
/// setup.
///
/// The motion is in the other one, at 0x800A9B04 every 0xB40, where the same
/// differ found about a thousand moving bytes per car. That array was
/// dismissed earlier on two bad readings: its slots 2 to 5 read as zeroes in a
/// launched race, which had one entrant rather than six; and a write watch
/// armed during loading caught CD_getsector and gzip filling 0x800A9D10, which
/// made it look like a buffer. It is a buffer, until the race reuses it.
///
/// So: 0x8015F894 stride 0x5000 is what a car is; 0x800A9B04 stride 0xB40 is
/// what it is doing.
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
