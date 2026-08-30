using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Finds every car on the track by what a car's position looks like.
///
/// Reading three samples of the live slot gave the shape. A car carries its
/// place as three words and a scale:
///
///     +0x00  X, hundreds of thousands, signed
///     +0x04  Z, millions, signed
///     +0x08  Y, small - height barely varies on a track
///     +0x20  4096, which is 1.0 in the twelve-bit fixed point the PS1 uses
///
/// The player's slot at 0x800A9D10 holds it and moves; the slot 0xB40 after it
/// holds a different, valid, unmoving one. The four slots after that read as
/// zeroes, so the six-of-a-kind the differ reported was the display lists at
/// +0x628 and not the cars - the third time that differ has counted display
/// lists and been believed.
///
/// So rather than trust a stride, this looks for the shape itself. Every place
/// in RAM that holds a plausible position with 4096 twenty bytes on is a car,
/// wherever it turns out to live, and the addresses that come back say what
/// the real array is - or that there is not one.
///
/// Off unless GT2_CAR_FIND is set.
/// </summary>
public static class CarFind
{
    static readonly bool Finding =
        Environment.GetEnvironmentVariable("GT2_CAR_FIND") is not (null or "");

    /// <summary>How long to wait, so the cars are placed and moving.</summary>
    static readonly int After =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_CAR_FIND_AFTER"), out int a) ? a : 480;

    const uint RamBase = 0x80000000u;
    const int RamSize = 0x00200000;

    /// <summary>Where the scale sits, relative to the position.</summary>
    const int ScaleAt = 0x20;
    const int Scale = 4096;

    /// <summary>What counts as a plausible place on a track, in fixed point.</summary>
    const int Nearest = 1000;
    const int Furthest = 40_000_000;

    /// <summary>How high a car may be before it is not a car.</summary>
    const int Highest = 200_000;

    /// <summary>How many to report before giving up on the signature.</summary>
    const int TooMany = 64;

    static int _frames;
    static bool _found;

    /// <summary>Called once per race frame.</summary>
    public static void FrameBegins(IMemory m)
    {
        if (!Finding || _found || m is not PSMemory ps) return;
        if (_frames++ < After) return;
        _found = true;

        var ram = ps.Ram;
        var hits = new List<(uint At, int X, int Z, int Y)>();

        for (int at = 0; at + ScaleAt + 4 <= RamSize && hits.Count <= TooMany; at += 4)
        {
            if (Word(ram, at + ScaleAt) != Scale) continue;

            int x = Word(ram, at), z = Word(ram, at + 4), y = Word(ram, at + 8);
            if (!Plausible(x) || !Plausible(z) || Math.Abs(y) > Highest) continue;

            hits.Add((RamBase + (uint)at, x, z, y));
        }

        Console.Error.WriteLine(
            $"[find] {hits.Count} place(s) look like a car's position at frame {_frames}:");

        uint previous = 0;
        foreach (var (place, x, z, y) in hits)
        {
            string step = previous == 0 ? "" : $"  (0x{place - previous:X} on)";
            Console.Error.WriteLine($"[find]   0x{place:X8}  x={x,12} z={z,12} y={y,8}{step}");
            previous = place;
        }
    }

    static bool Plausible(int v) => Math.Abs(v) >= Nearest && Math.Abs(v) <= Furthest;

    static int Word(ReadOnlySpan<byte> ram, int at) =>
        ram[at] | ram[at + 1] << 8 | ram[at + 2] << 16 | ram[at + 3] << 24;
}
