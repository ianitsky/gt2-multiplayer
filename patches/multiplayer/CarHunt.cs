using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Finds the six cars in RAM by watching what moves when they do.
///
/// Two attempts to reach a car's controls by reading have ended the same way:
/// the offsets that look right are reached through a register the caller
/// supplies, and IsAi at entrant +0x82 turned out not to be the flag that
/// decides who drives - clearing it left the second car racing exactly as
/// before. The human count is a local computed from the race's mode byte at
/// every use, so there is no field to write either.
///
/// Both of the remaining routes need the same thing first: where a car's live
/// state is. Feeding a remote player's input to the AI's slot needs the
/// controls; writing a remote player's position over the AI's needs the
/// transform. Neither is reachable by reading, and both are obvious to a
/// differ - a racing car's position changes every single frame, and almost
/// nothing else in two megabytes does.
///
/// So this compares RAM against the frame before, counts how often each word
/// changes, and reports the longest runs of words that changed nearly always.
/// Six runs at a constant stride are the six cars.
///
/// Off unless GT2_CAR_HUNT is set. It copies two megabytes a frame, which is
/// nothing to measure with and not something to leave on.
/// </summary>
public static class CarHunt
{
    static readonly bool Hunting =
        Environment.GetEnvironmentVariable("GT2_CAR_HUNT") is not (null or "");

    /// <summary>How many frames to watch before reporting.</summary>
    static readonly int Frames =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_CAR_HUNT_FRAMES"), out int n) ? n : 240;

    /// <summary>
    /// How many frames to let pass before watching at all.
    ///
    /// The first run started at the race's first frame, so most of its window
    /// was loading and the countdown with the cars sitting still. Only things
    /// that move while a car does not - display lists, timers - survived the
    /// test, and the six runs it found turned out to be GPU primitives: a
    /// pointer, then 0x40000006 and 0x00FFFFFF, twice over. A car's position
    /// moves every frame it is driven and none of the frames before that, so
    /// the countdown has to be behind the window rather than inside it.
    /// </summary>
    static readonly int After =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_CAR_HUNT_AFTER"), out int a) ? a : 420;

    /// <summary>Where the guest's RAM begins, and how much of it there is.</summary>
    const uint RamBase = 0x80000000u;
    const int RamSize = 0x00200000;

    /// <summary>
    /// How much of the time a word has to change to count as moving. Not every
    /// frame: a car's height barely changes on a straight, and a strict test
    /// would drop the very fields that say a car is a car.
    /// </summary>
    static readonly double Often =
        double.TryParse(Environment.GetEnvironmentVariable("GT2_CAR_HUNT_OFTEN"),
            System.Globalization.CultureInfo.InvariantCulture, out double o) ? o : 0.5;

    /// <summary>How many runs to name, longest first.</summary>
    const int Longest = 12;

    /// <summary>How far apart two runs may be and still be read as one.</summary>
    const int Joins = 16;

    static byte[]? _before;
    static int[]? _changed;
    static int _frames;
    static int _skipped;
    static bool _reported;

    /// <summary>What the six runs said the cars are spaced by.</summary>
    const uint CarZero = 0x800AA12Cu;
    const int CarStride = 0xB40;

    /// <summary>
    /// Where car zero's object is taken to begin.
    ///
    /// The display lists that first showed the stride sit at +0x628 into it,
    /// and the run just below them starts 0x10 past this - so this is the
    /// object's front, near enough to line six windows up against. It is a
    /// starting point for the report below, not a claim.
    /// </summary>
    static readonly uint CarBase =
        uint.TryParse(Environment.GetEnvironmentVariable("GT2_CAR_BASE"),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out uint b)
            ? b : CarZero - 0x628u;

    /// <summary>How many of the six a field has to move in to be worth naming.</summary>
    const int Most = 4;

    /// <summary>Called once per race frame, before anything else looks at RAM.</summary>
    public static void FrameBegins(IMemory m)
    {
        if (!Hunting || _reported || m is not PSMemory ps) return;
        if (_skipped++ < After) return;

        var now = ps.Ram;
        if (_before is null)
        {
            _before = now.ToArray();
            _changed = new int[RamSize / 4];
            return;
        }

        var before = _before;
        var changed = _changed!;
        for (int w = 0; w < changed.Length; w++)
        {
            int at = w * 4;
            if (now[at] != before[at] || now[at + 1] != before[at + 1]
                || now[at + 2] != before[at + 2] || now[at + 3] != before[at + 3])
                changed[w]++;
        }
        now.CopyTo(before);

        if (++_frames < Frames) return;
        _reported = true;
        Report(changed);
    }

    /// <summary>
    /// The six car windows side by side, and the offsets that move in most of
    /// them.
    ///
    /// The largest runs in a race are display lists and the stack, and printing
    /// only the largest buried what was being looked for. What names a car's
    /// own field is not size: it is the same offset moving in car after car.
    /// </summary>
    static void SayTheGrid(int[] changed, int enough)
    {
        Console.Error.WriteLine(
            $"[hunt] six windows of 0x{CarStride:X} from 0x{CarBase:X8}:");

        var perOffset = new Dictionary<int, List<int>>();
        for (int car = 0; car < 6; car++)
        {
            uint at = CarBase + (uint)(car * CarStride);
            int moved = 0;
            for (int i = 0; i < CarStride; i += 4)
            {
                int w = (int)((at + (uint)i - RamBase) / 4);
                if (w < 0 || w >= changed.Length || changed[w] < enough) continue;
                moved++;
                if (!perOffset.TryGetValue(i, out var cars)) perOffset[i] = cars = [];
                cars.Add(car);
            }
            Console.Error.WriteLine($"[hunt]   car {car} 0x{at:X8}: {moved} words moving");
        }

        var shared = perOffset.Where(p => p.Value.Count >= Most).OrderBy(p => p.Key).ToList();
        Console.Error.WriteLine(
            $"[hunt] {shared.Count} offset(s) move in at least {Most} of the six:");

        // Printed as runs, because a coordinate is three words and a matrix is
        // nine, and a list of singletons would hide both.
        int start = -1, last = -2;
        foreach (var (off, cars) in shared.Select(p => (p.Key, p.Value)).Append((-1, new List<int>())))
        {
            if (off == last + 4) { last = off; continue; }
            if (start >= 0)
                Console.Error.WriteLine(
                    $"[hunt]   +0x{start:X3} .. +0x{last + 3:X3}  ({(last - start) / 4 + 1} words)");
            start = off; last = off;
        }
    }

    static void Report(int[] changed)
    {
        int enough = (int)(_frames * Often);

        // Runs of words that all move, joined across small quiet gaps: a car's
        // state is not one word and it is not solid either.
        var runs = new List<(int Start, int End, int Words)>();
        int start = -1, quiet = 0, words = 0;
        for (int w = 0; w < changed.Length; w++)
        {
            if (changed[w] >= enough)
            {
                if (start < 0) { start = w; words = 0; }
                quiet = 0;
                words++;
            }
            else if (start >= 0 && ++quiet > Joins)
            {
                runs.Add((start, w - quiet, words));
                start = -1;
            }
        }
        if (start >= 0) runs.Add((start, changed.Length - 1, words));

        Console.Error.WriteLine(
            $"[hunt] {_frames} frames, {changed.Count(x => x >= enough)} words moved in"
            + $" {Often:P0} of them, in {runs.Count} run(s):");

        foreach (var run in runs.OrderByDescending(r => r.Words).Take(Longest))
        {
            uint at = RamBase + (uint)(run.Start * 4);

            // Where it falls inside a car, when it falls inside one at all.
            // Six runs sharing an offset are the same field of six cars, which
            // is the shape being looked for and is not readable from addresses.
            string inCar = at >= CarZero - (uint)CarStride && at < CarZero + (uint)(CarStride * 6)
                ? $"  car {(int)(at - (CarZero - (uint)CarStride)) / CarStride - 1}"
                  + $" +0x{(at - CarZero) % (uint)CarStride:X3}"
                : "";

            Console.Error.WriteLine(
                $"[hunt]   0x{at:X8} .. 0x{RamBase + (uint)(run.End * 4 + 3):X8}"
                + $"  {(run.End - run.Start + 1) * 4} bytes, {run.Words} moving{inCar}");
        }

        SayTheGrid(changed, enough);
    }
}
