using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Finds every car on the track by what a car's position looks like.
///
/// The first signature asked for 4096 twenty bytes past the position - 1.0 in
/// the PS1's fixed point - and found the two cars of a launched race and none
/// at all of a walked one on the same track in the same mode. So that 4096 was
/// a neighbour that happened to be there, and the addresses it found were
/// right by luck.
///
/// What repeated is the position itself. 0x800A9D10 and 0x800A9D34 held the
/// same three words, and so did 0x800AA850 and 0x800AA874. Three matching
/// words at a fixed distance is a far less likely accident than one constant,
/// and it is a fact about a car rather than about whatever was allocated next
/// to one.
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

    /// <summary>How far on the second copy of the position sits.</summary>
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

    /// <summary>
    /// Every address holding a position written twice.
    ///
    /// Separate from the reporting so a test can reach it: the scan walks every
    /// word of two megabytes of raw RAM, which is exactly the sort of thing
    /// that meets 0x80000000 and finds out what Math.Abs does with it.
    /// </summary>
    public static IReadOnlyList<uint> Scan(IMemory m)
    {
        var hits = new List<uint>();
        if (m is not PSMemory ps) return hits;

        var ram = ps.Ram;
        for (int at = 0; at + CopyAt + 12 <= RamSize && hits.Count <= TooMany; at += 4)
        {
            int x = Word(ram, at), z = Word(ram, at + 4), y = Word(ram, at + 8);
            if (!Plausible(x) || !Plausible(z) || !LowEnough(y)) continue;

            if (Word(ram, at + CopyAt) != x || Word(ram, at + CopyAt + 4) != z
                || Word(ram, at + CopyAt + 8) != y) continue;

            hits.Add(RamBase + (uint)at);
        }
        return hits;
    }

    /// <summary>Called once per race frame.</summary>
    public static void FrameBegins(IMemory m)
    {
        if (!Finding || _found || m is not PSMemory ps) return;
        if (_frames++ < After) return;
        _found = true;

        var ram = ps.Ram;
        var hits = Scan(m);

        Console.Error.WriteLine(
            $"[find] {hits.Count} place(s) hold a position twice, 0x{CopyAt:X} apart,"
            + $" at frame {_frames}:");

        uint previous = 0;
        foreach (uint place in hits)
        {
            int at = (int)(place - RamBase);
            Console.Error.WriteLine(
                $"[find]   0x{place:X8}  x={Word(ram, at),12} z={Word(ram, at + 4),12}"
                + $" y={Word(ram, at + 8),8}"
                + (previous == 0 ? "" : $"  (0x{place - previous:X} on)"));
            previous = place;

            // The words around it, so this can be checked rather than believed.
            // Four probes running have now found something that was not a car.
            var line = new System.Text.StringBuilder();
            for (int i = -Around; i < Around; i++)
            {
                int w = at + i * 4;
                if (w < 0 || w + 4 > RamSize) continue;
                line.Append(i == 0 ? $" [{Word(ram, w)}]" : $" {Word(ram, w)}");
            }
            Console.Error.WriteLine($"[find]     from -0x{Around * 4:X}:{line}");
        }
    }

    /// <summary>
    /// Whether a word could be a coordinate.
    ///
    /// Widened to long before the absolute value: scanning raw RAM reaches
    /// 0x80000000 sooner or later, and Math.Abs of the most negative int throws
    /// rather than returning anything - which crashed a race a few frames after
    /// the scan began.
    /// </summary>
    static bool Plausible(int v)
    {
        long size = Math.Abs((long)v);
        return size >= Nearest && size <= Furthest;
    }

    /// <summary>The same widening, for the height.</summary>
    static bool LowEnough(int v) => Math.Abs((long)v) <= Highest;

    static int Word(ReadOnlySpan<byte> ram, int at) =>
        ram[at] | ram[at + 1] << 8 | ram[at + 2] << 16 | ram[at + 3] << 24;
}
