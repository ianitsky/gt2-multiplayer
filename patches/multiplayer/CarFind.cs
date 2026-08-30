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

    /// <summary>
    /// How far on the second copy of the position sits.
    ///
    /// The first signature asked for 4096 twenty bytes past the position -
    /// 1.0 in the PS1's fixed point - and found the two cars of a launched
    /// race and none at all of a walked one on the same track in the same
    /// mode. So that 4096 was a neighbour, not part of a car.
    ///
    /// What did repeat is the position itself: 0x800A9D10 and 0x800A9D34 held
    /// the same three words, and so did 0x800AA850 and 0x800AA874. Three
    /// matching words at a fixed distance is a far less likely accident than
    /// one constant, and it is a thing about a car rather than about whatever
    /// was allocated next to one.
    /// </summary>
    const int CopyAt = 0x24;

    /// <summary>What counts as a plausible place on a track, in fixed point.</summary>
    const int Nearest = 1000;
    const int Furthest = 40_000_000;

    /// <summary>How high a car may be before it is not a car.</summary>
    const int Highest = 200_000;

    /// <summary>How many to report before giving up on the signature.</summary>
    const int TooMany = 40;

    /// <summary>How much either side of a hit to print, so it can be judged.</summary>
    const int Around = 12;

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

        for (int at = 0; at + CopyAt + 12 <= RamSize && hits.Count <= TooMany; at += 4)
        {
            int x = Word(ram, at), z = Word(ram, at + 4), y = Word(ram, at + 8);
            if (!Plausible(x) || !Plausible(z) || Math.Abs(y) > Highest) continue;

            if (Word(ram, at + CopyAt) != x || Word(ram, at + CopyAt + 4) != z
                || Word(ram, at + CopyAt + 8) != y) continue;

            hits.Add((RamBase + (uint)at, x, z, y));
        }

        Console.Error.WriteLine(
            $"[find] {hits.Count} place(s) hold a position twice, 0x{CopyAt:X} apart,"
            + $" at frame {_frames}:");

        uint previous = 0;
        foreach (var (place, x, z, y) in hits)
        {
            Console.Error.WriteLine(
                $"[find]   0x{place:X8}  x={x,12} z={z,12} y={y,8}"
                + (previous == 0 ? "" : $"  (0x{place - previous:X} on)"));
            previous = place;

            // The words around it, so this can be checked rather than believed.
            // Four probes running have now found something that was not a car.
            var line = new System.Text.StringBuilder();
            for (int i = -Around; i < Around; i++)
            {
                int at = (int)(place - RamBase) + i * 4;
                if (at < 0 || at + 4 > RamSize) continue;
                if (i == 0) line.Append(" [");
                line.Append($"{Word(ram, at)}");
                line.Append(i == 0 ? "]" : " ");
            }
            Console.Error.WriteLine($"[find]     -0x{Around * 4:X}: {line}");
        }
    }

    static bool Plausible(int v) => Math.Abs(v) >= Nearest && Math.Abs(v) <= Furthest;

    static int Word(ReadOnlySpan<byte> ram, int at) =>
        ram[at] | ram[at + 1] << 8 | ram[at + 2] << 16 | ram[at + 3] << 24;
}
