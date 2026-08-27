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

    /// <summary>Where the guest's RAM begins, and how much of it there is.</summary>
    const uint RamBase = 0x80000000u;
    const int RamSize = 0x00200000;

    /// <summary>
    /// How much of the time a word has to change to count as moving. Not every
    /// frame: a car's height barely changes on a straight, and a strict test
    /// would drop the very fields that say a car is a car.
    /// </summary>
    const double Often = 0.75;

    /// <summary>How many runs to name, longest first.</summary>
    const int Longest = 12;

    /// <summary>How far apart two runs may be and still be read as one.</summary>
    const int Joins = 16;

    static byte[]? _before;
    static int[]? _changed;
    static int _frames;
    static bool _reported;

    /// <summary>Called once per race frame, before anything else looks at RAM.</summary>
    public static void FrameBegins(IMemory m)
    {
        if (!Hunting || _reported || m is not PSMemory ps) return;

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
            Console.Error.WriteLine(
                $"[hunt]   0x{RamBase + (uint)(run.Start * 4):X8}"
                + $" .. 0x{RamBase + (uint)(run.End * 4 + 3):X8}"
                + $"  {(run.End - run.Start + 1) * 4} bytes, {run.Words} of them moving");

        // The stride between runs is what says whether they are six of a kind.
        var order = runs.OrderBy(r => r.Start).ToList();
        for (int i = 1; i < order.Count && i <= Longest; i++)
            Console.Error.WriteLine(
                $"[hunt]   run {i} begins 0x{(order[i].Start - order[i - 1].Start) * 4:X} after run {i - 1}");
    }
}
