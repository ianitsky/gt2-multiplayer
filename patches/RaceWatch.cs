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
        MaybeSwapPlayer(m);
        MaybeMoveMarker(m);
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
    /// Which slot to move the player's entrant into, when GT2_RACE_SWAP names
    /// one. The whole 208-byte record is exchanged with whatever is there.
    ///
    /// This settles how the game decides which car is the player's. If the
    /// player still drives slot 0 after the swap, the player is whoever is in
    /// slot 0 and a multiplayer grid cannot simply give each machine its own
    /// slot. If the player follows their record to its new slot, something in
    /// the record marks them - the driver-name field, filled with "0" for the
    /// player and left empty for the opponents, is the candidate - and each
    /// machine can take a different slot, which is what puts six players in six
    /// different places on the grid.
    /// </summary>
    static readonly int Swap =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_RACE_SWAP"), out int slot) ? slot : -1;

    static bool _swapped;

    /// <summary>
    /// Which slot to move the player's driver-name marker to, when
    /// GT2_RACE_MARKER names one. Nothing else is touched.
    ///
    /// Swapping whole records breaks the race - a record carries state that
    /// belongs to the slot, not to the entrant, and the car vanishes. So move
    /// the one field that distinguishes the player from the opponents: slot 0
    /// carries "0" in its driver-name field and the rest are empty. If the
    /// player ends up driving another car, that field is the marker and each
    /// machine can nominate a different slot as its own.
    /// </summary>
    static readonly int Marker =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_RACE_MARKER"), out int slot) ? slot : -1;

    static bool _markerMoved;

    /// <summary>
    /// What to write into the player's byte at entrant +0x16, when
    /// GT2_RACE_START names a value.
    ///
    /// The player's entrant carries 6 there and starts 6th; every opponent
    /// carries 0. The race overlay reads exactly this byte - block + 0x5A is
    /// entrant 0 + 0x16 - and branches on whether it is zero. If it is the
    /// starting position, a multiplayer grid is six machines each writing a
    /// different value, which is the lever this whole hunt is for.
    /// </summary>
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
    ///
    /// Across the six entrants this field holds a complete permutation of 0..5,
    /// and the player - who starts sixth - holds 5. Two neighbours look like
    /// what they would have to be for a multiplayer race: +0x82 is 0 for the
    /// player and 1 for every opponent, and +0x42 is 0 for the player and 100
    /// for the rest, which is the shape of an AI skill.
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

    static void MaybeMoveMarker(IMemory m)
    {
        if (Marker <= 0 || Marker >= Entrants || _markerMoved) return;

        for (int i = 0; i < Entrants; i++)
            if (m.ReadU32(Context + (uint)(FirstEntrant + i * EntrantSize) + EntrantCarId) == 0) return;

        // What this used to copy was the header, not an entrant: the entrant
        // records begin at 0x5C, and the "name" that only entrant 0 appeared to
        // have was the header's own text at 0x44. Kept, with the offsets
        // corrected, since a per-entrant field is still where a player marker
        // would live.
        uint from = Context + FirstEntrant;
        uint to = Context + (uint)(FirstEntrant + Marker * EntrantSize);
        for (uint i = 0; i < 24; i++)
            m.WriteU8(to + i, m.ReadU8(from + i));

        _markerMoved = true;
        Console.Error.WriteLine(
            $"[race] driver name copied from 0 to {Marker}:{Environment.NewLine}{Report(m, Context)}");
    }

    static void MaybeSwapPlayer(IMemory m)
    {
        if (Swap <= 0 || Swap >= Entrants || _swapped) return;

        for (int i = 0; i < Entrants; i++)
            if (m.ReadU32(Context + (uint)(FirstEntrant + i * EntrantSize) + EntrantCarId) == 0) return;

        uint a = Context + FirstEntrant;
        uint b = Context + (uint)(FirstEntrant + Swap * EntrantSize);
        for (uint i = 0; i < EntrantSize; i++)
        {
            byte left = m.ReadU8(a + i), right = m.ReadU8(b + i);
            m.WriteU8(a + i, right);
            m.WriteU8(b + i, left);
        }
        _swapped = true;
        Console.Error.WriteLine(
            $"[race] entrant 0 swapped with {Swap}:{Environment.NewLine}{Report(m, Context)}");
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
