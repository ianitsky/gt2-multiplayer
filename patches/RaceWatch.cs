using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port;

/// <summary>
/// Reports the race the game is about to install.
///
/// gt2_main_func21 installs a race by copying a 1420-byte record from A0 into
/// the race context at 0x801D585C. That makes it the one place where a race is
/// fully described before anything starts, and the multiplayer race has to
/// produce a record of exactly this shape - so the first thing to know is what
/// the game's own arcade race puts there.
///
/// What the layout gives up: the race key and course name are plain text, and
/// the entrants are six fixed-size records each carrying a name and the packed
/// five-character car id. Entrant order is what a grid slot would be read from,
/// if entrant order is grid order - which watching a real race is what settles.
///
/// Off unless GT2_RACE_WATCH is set.
/// </summary>
public static class RaceWatch
{
    const int RecordSize = 0x58C;      // the whole race definition
    const int KeyOffset = 0x10;        // "MSC0002" and the like
    const int CourseOffset = 0x20;     // the course as shown on screen
    const int Count = 0x5A;            // how many entrants the race has
    const int FirstEntrant = 0x5C;     // where the entrant records begin
    const int EntrantSize = 0xD0;
    const int EntrantCarId = 0x00;     // packed five-character code
    const int EntrantCarName = 0x90;
    const int Entrants = 6;

    /// <summary>The alphabet a packed car id is written in, five characters to a word.</summary>
    const string Alphabet = "-0123456789abcdefghijklmnopqrstuvwxyz";

    static readonly bool Watching =
        Environment.GetEnvironmentVariable("GT2_RACE_WATCH") is not (null or "");

    static int _seen;

    /// <summary>Where an installed race lives, which is where it is read from.</summary>
    const uint Context = 0x801D585Cu;

    static string? _installed;

    /// <summary>
    /// Called once a frame. The hook below assumes the arcade installs a race
    /// the same way the attract demo does, which is not proven - so watch the
    /// destination too, and catch the race whatever route it arrives by.
    /// </summary>
    public static void Tick(IMemory m)
    {
        if (!Watching) return;

        MaybeWriteGrid(m);
        MaybePlace(m);

        string now = Describe(m, Context);
        if (now == _installed) return;
        _installed = now;

        // A block of zeroes is the gap between races, not a race.
        if (now.TrimEnd('|', ' ').Length == 0) return;

        Console.Error.WriteLine($"[race] context now holds:{Environment.NewLine}{Report(m, Context)}");
        Dump(m, Context, $"race-context-{++_seen}.bin");
    }

    /// <summary>
    /// Cars to put in the opponents' slots, as five-character ids, when
    /// GT2_RACE_GRID names them - "n24vn,n24vn,n24vn,n24vn,n24vn" fills every
    /// opponent slot with the same Skyline, which is unmistakable on the grid.
    ///
    /// This is the experiment phase 2 rests on: a multiplayer race is this
    /// block with the other players' cars in it, so the first thing to prove is
    /// that writing the block from outside the menu reaches the race at all.
    /// </summary>
    static readonly string[] Grid =
        (Environment.GetEnvironmentVariable("GT2_RACE_GRID") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static bool _gridWritten;

    /// <summary>
    /// A place for each entrant in turn, when GT2_RACE_PLACES lists them -
    /// "1,2,3,4,5,6" gives the player pole and lines the opponents up behind.
    /// A single number still works and applies to the player alone.
    /// </summary>
    static readonly int[] Places =
        (Environment.GetEnvironmentVariable("GT2_RACE_PLACES")
         ?? Environment.GetEnvironmentVariable("GT2_RACE_START") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(v => int.TryParse(v, out int n) ? n : -1)
        .ToArray();

    /// <summary>
    /// Where an entrant carries its place on the grid, counted from zero.
    /// Across the six it is a complete permutation of 0..5.
    /// </summary>
    const int EntrantPlace = 0x8D;

    /// <summary>0 for the car the human drives, 1 for the ones the game drives.</summary>
    const int EntrantIsAi = 0x82;

    static bool _placed;

    static void MaybePlace(IMemory m)
    {
        if (Places.Length == 0 || _placed) return;

        for (int i = 0; i < Entrants; i++)
            if (m.ReadU32(Context + (uint)(FirstEntrant + i * EntrantSize) + EntrantCarId) == 0) return;

        // Swapped, not assigned: the six places are a permutation of 0..5, and
        // handing two entrants the same one is not a grid the game can build.
        // Giving the player place P means giving whoever holds P the player's.
        int wanted = Places[0];
        uint player = Context + FirstEntrant + EntrantPlace;
        byte was = m.ReadU8(player);
        for (int i = 1; i < Entrants; i++)
        {
            uint other = Context + (uint)(FirstEntrant + i * EntrantSize) + EntrantPlace;
            if (m.ReadU8(other) != wanted) continue;
            m.WriteU8(other, was);
            break;
        }
        m.WriteU8(player, (byte)wanted);
        _placed = true;

        var places = string.Join(" ", Enumerable.Range(0, Entrants)
            .Select(i => $"{m.ReadU8(Context + (uint)(FirstEntrant + i * EntrantSize) + EntrantPlace)}"
                       + (m.ReadU8(Context + (uint)(FirstEntrant + i * EntrantSize) + EntrantIsAi) == 0 ? "*" : "")));
        Console.Error.WriteLine($"[race] grid places now read: {places}   (* is the human's)");
    }



    static void MaybeWriteGrid(IMemory m)
    {
        if (Grid.Length == 0 || _gridWritten) return;

        // Only once the menu has finished filling the block: overwriting it
        // while the player is still choosing would just be overwritten back.
        for (int i = 0; i < Entrants; i++)
            if (m.ReadU32(Context + (uint)(FirstEntrant + i * EntrantSize) + EntrantCarId) == 0) return;

        for (int i = 0; i < Grid.Length && i + 1 < Entrants; i++)
        {
            uint entrant = Context + (uint)(FirstEntrant + (i + 1) * EntrantSize);
            m.WriteU32(entrant + EntrantCarId, Pack(Grid[i]));
        }
        _gridWritten = true;
        Console.Error.WriteLine(
            $"[race] opponents replaced with {string.Join(" ", Grid)}:{Environment.NewLine}{Report(m, Context)}");
    }

    /// <summary>A five-character car id packed the way the game stores it.</summary>
    static uint Pack(string id)
    {
        uint packed = 0;
        int[] shifts = [24, 18, 12, 6, 0];
        for (int i = 0; i < 5 && i < id.Length; i++)
        {
            int index = Alphabet.IndexOf(id[i]);
            if (index >= 0) packed |= (uint)index << shifts[i];
        }
        return packed;
    }

    /// <summary>Just enough of a race to tell one from another.</summary>
    static string Describe(IMemory m, uint at)
    {
        var parts = new List<string> { Text(m, at + KeyOffset, 16), Text(m, at + CourseOffset, 32) };
        for (int i = 0; i < Entrants; i++)
            parts.Add(m.ReadU32(at + (uint)(FirstEntrant + i * EntrantSize) + EntrantCarId).ToString("X8"));
        return string.Join("|", parts);
    }

    /// <summary>Pre-hook on gt2_main_func21; the install itself runs as normal after it.</summary>
    public static void Installing(CpuContext c, IMemory m)
    {
        if (!Watching) return;

        uint at = c.A0;
        _seen++;

        Console.Error.WriteLine($"[race] install from 0x{at:X8}:{Environment.NewLine}{Report(m, at)}");
        Dump(m, at, $"race-install-{++_seen}.bin");
    }

    /// <summary>The fields of a race that are already understood, as text.</summary>
    static string Report(IMemory m, uint at)
    {
        var lines = new List<string>
        {
            $"[race]   key    {Text(m, at + KeyOffset, 16)}",
            $"[race]   course {Text(m, at + CourseOffset, 32)}",
            $"[race]   {m.ReadU8(at + Count)} entrants",
        };
        for (int i = 0; i < Entrants; i++)
        {
            uint entrant = at + (uint)(FirstEntrant + i * EntrantSize);
            uint id = m.ReadU32(entrant + EntrantCarId);
            lines.Add($"[race]   {i}: {Unpack(id),-6} {Text(m, entrant + EntrantCarName, 32)}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The whole record to a file. The fields Report names are the ones already
    /// understood; the rest is what still has to be worked out before a race can
    /// be built rather than copied.
    /// </summary>
    static void Dump(IMemory m, uint at, string path)
    {
        var bytes = new byte[RecordSize];
        for (int i = 0; i < RecordSize; i++) bytes[i] = m.ReadU8(at + (uint)i);
        try
        {
            File.WriteAllBytes(path, bytes);
            Console.Error.WriteLine($"[race]   {RecordSize} bytes written to {path}");
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"[race]   could not write {path}: {e.Message}");
        }
    }

    /// <summary>A packed car id back as the five characters the game names it by.</summary>
    static string Unpack(uint id)
    {
        if (id == 0) return "-";
        Span<char> chars = stackalloc char[5];
        int[] shifts = [24, 18, 12, 6, 0];
        for (int i = 0; i < 5; i++)
        {
            int index = (int)((id >> shifts[i]) & 0x3F);
            chars[i] = index < Alphabet.Length ? Alphabet[index] : '?';
        }
        return new string(chars);
    }

    static string Text(IMemory m, uint at, int max)
    {
        var chars = new List<char>();
        for (int i = 0; i < max; i++)
        {
            byte b = m.ReadU8(at + (uint)i);
            if (b == 0) break;
            chars.Add(b is >= 0x20 and < 0x7F ? (char)b : '.');
        }
        return new string([.. chars]);
    }
}
