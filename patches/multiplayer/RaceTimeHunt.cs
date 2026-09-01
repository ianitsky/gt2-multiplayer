using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Finds a number the game is holding, by looking for it.
///
/// Written for the race time, which three passes of the moving-field probe
/// could not name. Two counters did turn up - the frame count at 0x800A8D72,
/// and 0x800A8C64 which is exactly twice it - and neither is the time on the
/// screen: both were already running before the lights went green, and the
/// screen's clock starts at zero there.
///
/// Which is also why the probe missed it. A clock that is reset at the green
/// light *falls* once, and a search for fields that never fall throws away the
/// one field it was looking for. Rather than teach that probe about
/// countdowns, this asks the question the other way round: the race is over,
/// the screen said 1:31.150, so find every place in RAM holding 1:31.150.
///
/// The encoding is unknown too, so several are tried at once. A time of
/// 91.150 seconds is 91150 in milliseconds, 5469 in sixtieths, 2734 in
/// thirtieths - and the game draws at thirty while counting in sixtieths, so
/// all three are live possibilities. GT2_FIND takes them as a plain list and
/// the addresses come back named by which one matched.
///
/// Scanned at the moment the race overlay is replaced, which is the last
/// instant the race's own memory is still standing - see BackToTheLobby.
/// </summary>
public static class RaceTimeHunt
{
    /// <summary>The numbers to look for, as a comma-separated list.</summary>
    static readonly int[] Wanted =
        [.. (Environment.GetEnvironmentVariable("GT2_FIND") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part.Trim(), out int n) ? n : 0)
            .Where(n => n != 0)];

    /// <summary>
    /// Where to look, which is now everywhere.
    ///
    /// It began as the race context, the cars and the globals around them -
    /// 0x800A0000 to 0x800B0000, everything the race was thought to be kept in.
    /// A race that ended at 2:24.009 held that number nowhere in those 64
    /// kilobytes, in milliseconds, sixtieths, seventy-fifths, hundredths or
    /// thirtieths, and no offset held six per-car values that could be six
    /// finishing times. So the time is kept somewhere else, and guessing where
    /// has now cost more than reading all of it.
    ///
    /// A snapshot of the whole two megabytes is a two-megabyte file and one
    /// pass, which is cheaper than another race - and it can ride along with a
    /// race being run for some other reason.
    /// </summary>
    static readonly uint From =
        uint.TryParse(Environment.GetEnvironmentVariable("GT2_FIND_FROM"),
                      System.Globalization.NumberStyles.HexNumber, null, out uint at)
            ? at : 0x80000000u;

    static readonly uint To =
        uint.TryParse(Environment.GetEnvironmentVariable("GT2_FIND_TO"),
                      System.Globalization.NumberStyles.HexNumber, null, out uint to)
            ? to : 0x80200000u;

    /// <summary>
    /// How many hits to print per number. A time is one address; a hundred
    /// addresses means the number was too small to be distinctive, which is
    /// itself worth knowing.
    /// </summary>
    const int AtMost = 24;

    /// <summary>
    /// Where to write the region instead of searching it.
    ///
    /// Searching needs the number up front, and the number is on a results
    /// screen nobody has read yet - so the first run cannot search for
    /// anything. A snapshot has no such problem: keep the race's last memory,
    /// be told what the screen said afterwards, and look for it here in as many
    /// encodings as it takes. One race rather than two, and the second guess
    /// costs nothing.
    /// </summary>
    static readonly string Keep =
        Environment.GetEnvironmentVariable("GT2_DUMP_END") ?? "";

    /// <summary>Looks for every wanted number, as a word and as a halfword.</summary>
    public static void Look(IMemory m)
    {
        if (Keep.Length > 0) Snapshot(m);
        if (Wanted.Length == 0) return;

        Console.Error.WriteLine(
            $"[find] looking through 0x{From:X8}..0x{To:X8} for "
            + string.Join(", ", Wanted));

        foreach (int wanted in Wanted)
        {
            var words = new List<uint>();
            var halves = new List<uint>();

            for (uint at = From; at + 4u <= To; at += 2u)
            {
                if (halves.Count < AtMost && (short)m.ReadU16(at) == wanted) halves.Add(at);
                if (words.Count < AtMost && (int)m.ReadU32(at) == wanted) words.Add(at);
            }

            Console.Error.WriteLine(
                $"[find] {wanted}: as a word at {Say(words)}"
                + $"{Environment.NewLine}[find] {wanted}: as a halfword at {Say(halves)}");
        }
    }

    /// <summary>Writes the watched region out, so it can be searched at leisure.</summary>
    static void Snapshot(IMemory m)
    {
        string path = Keep == "1"
            ? Path.Combine("captures", "race-end.bin")
            : Keep;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var bytes = new byte[To - From];
        for (uint i = 0; i < bytes.Length; i++) bytes[i] = m.ReadU8(From + i);
        File.WriteAllBytes(path, bytes);

        Console.Error.WriteLine(
            $"[find] the race's last memory, 0x{From:X8}..0x{To:X8}, is in {path}");
    }

    static string Say(List<uint> found) =>
        found.Count == 0 ? "nowhere"
        : string.Join(" ", found.Select(a => $"0x{a:X8}"))
          + (found.Count >= AtMost ? " (and more)" : "");
}
