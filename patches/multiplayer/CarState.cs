using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Watches the six cars the differ found.
///
/// CarHunt compared two megabytes against the frame before for 240 frames and
/// reported six runs of moving words, each exactly 116 bytes, at a constant
/// stride of 0xB40:
///
///     0x800AA12C  0x800AAC6C  0x800AB7AC  0x800AC2EC  0x800ACE2C  0x800AD96C
///
/// Six of a kind, evenly spaced, moving every frame while the cars move. That
/// is the grid, and 0x800AA12C is the part of car zero that changes - the
/// object itself will begin earlier, since a car's mass and gear ratios do not
/// move and so did not show up.
///
/// What each word means is the next question, and the cheapest answer is to
/// print the window a few times and read it: a coordinate is large and drifts
/// smoothly, a heading wraps, a velocity swings around zero, and a wheel angle
/// tracks the stick. Printing car zero beside car one shows which fields the
/// player's car and an AI's car keep in the same places.
///
/// Off unless GT2_CAR_STATE is set.
/// </summary>
public static class CarState
{
    static readonly bool Watching =
        Environment.GetEnvironmentVariable("GT2_CAR_STATE") is not (null or "");

    /// <summary>Where car zero's moving state begins, and how far to the next car.</summary>
    public const uint FirstCar = 0x800AA12Cu;
    public const int CarStride = 0xB40;
    public const int MovingBytes = 116;

    /// <summary>How many cars the grid holds.</summary>
    public const int Cars = 6;

    /// <summary>How often to print, in race frames, and how many times.</summary>
    const int Every = 90;
    const int Times = 5;

    static int _frames;
    static int _printed;

    /// <summary>Called once per race frame.</summary>
    public static void FrameBegins(IMemory m)
    {
        if (!Watching || _printed >= Times) return;
        if (_frames++ % Every != 0) return;

        _printed++;
        Console.Error.WriteLine($"[car] sample {_printed} at frame {_frames - 1}:");
        Say(m, 0);
        Say(m, 1);
    }

    static void Say(IMemory m, int car)
    {
        uint at = FirstCar + (uint)(car * CarStride);
        var words = new System.Text.StringBuilder();
        for (uint i = 0; i < MovingBytes; i += 4)
            words.Append($"{(int)m.ReadU32(at + i),12}");

        Console.Error.WriteLine($"[car]   car {car} 0x{at:X8}:{words}");
    }
}
