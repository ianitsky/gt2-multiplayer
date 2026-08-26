using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// The game's own list of the courses a race can be set to.
///
/// Found by following what the arcade does with the course. The parameter
/// builder hands [block+0x1B8] to 0x8005E590, which stores it at
/// [race+0x40], resolves it through 0x80060EB4 and copies the name it finds
/// into [race+0x20] - which is where a captured race block reads "Tahiti Road".
///
/// 0x80060EB4 is a linear search over a table in RAM at 0x801E18E0: a count as
/// a u16 at +0x06, then entries of 0x18 bytes from +0x08, each holding a
/// pointer to its name at +0x00 and the value the block carries at +0x04. So
/// the course is not a string in the block and not an index into anything
/// static - it is a number the game will look up in a roster it has already
/// built, and every course a race could run is in there with its number.
///
/// That is what makes a room's course reachable without capturing a parameter
/// block per track: read the roster, find the course, write its number.
/// </summary>
public static class CourseRoster
{
    /// <summary>The roster's header, whose +0x06 is how many entries follow.</summary>
    const uint Table = 0x801E18E0u;
    const uint Count = 0x06u;
    const uint FirstEntry = 0x08u;
    const int EntrySize = 0x18;

    /// <summary>Where an entry keeps its name and the number the block carries.</summary>
    const uint NameIn = 0x00u;
    const uint IdIn = 0x04u;

    /// <summary>More than the disc has, as a guard against reading a table that is not one.</summary>
    const int TooMany = 64;

    public sealed record Entry(int Index, uint Id, string Name);

    /// <summary>
    /// Every course the game has ready, or nothing when the roster has not been
    /// built yet - which is a fact about when this is called, not an error.
    /// </summary>
    public static IReadOnlyList<Entry> Read(IMemory m)
    {
        int count = m.ReadU16(Table + Count);
        if (count <= 0 || count > TooMany) return [];

        var found = new List<Entry>(count);
        for (int i = 0; i < count; i++)
        {
            uint entry = Table + FirstEntry + (uint)(i * EntrySize);
            uint name = m.ReadU32(entry + NameIn);
            found.Add(new Entry(i, m.ReadU32(entry + IdIn), ReadString(m, name)));
        }
        return found;
    }

    /// <summary>
    /// The number for a course named the way the game names it. Names repeat -
    /// the disc has a Tahiti Road forwards, reversed and two-player - so this
    /// answers with the first, and <see cref="Say"/> is what shows the rest.
    /// </summary>
    public static bool TryFind(IMemory m, string name, out uint id)
    {
        id = 0u;
        if (name.Length == 0) return false;

        foreach (var course in Read(m))
        {
            if (!string.Equals(course.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            id = course.Id;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Prints the roster, and the header raw when there is no roster to print.
    ///
    /// The first version said "no roster yet" for a count of zero and for a
    /// count of two hundred alike, so a run that found the table in a shape
    /// this did not expect was indistinguishable from one that found nothing.
    /// What the header actually holds is the thing worth reporting.
    /// </summary>
    public static void Say(IMemory m, string when)
    {
        if (!_said.Add(when)) return;

        int count = m.ReadU16(Table + Count);
        var courses = Read(m);

        if (courses.Count == 0)
        {
            var head = new System.Text.StringBuilder();
            for (uint i = 0; i < 0x20; i++) head.Append($"{m.ReadU8(Table + i):X2} ");
            Console.Error.WriteLine(
                $"[course] {when}: the roster at 0x{Table:X8} says {count} entries"
                + $" - {(count == 0 ? "not built" : count > TooMany ? "more than this will trust" : "unreadable")}");
            Console.Error.WriteLine($"[course]   0x{Table:X8}: {head.ToString().TrimEnd()}");
            return;
        }

        Console.Error.WriteLine($"[course] {when}: the game has {courses.Count} course(s) ready:");
        foreach (var course in courses)
            Console.Error.WriteLine($"[course]   {course.Index,2}. 0x{course.Id:X8}  {course.Name}");
    }

    static readonly HashSet<string> _said = [];

    /// <summary>How long a course name is allowed to be before this stops reading.</summary>
    const int LongestName = 64;

    static string ReadString(IMemory m, uint at)
    {
        if (at is < 0x80000000u or >= 0x80200000u) return "";

        var text = new System.Text.StringBuilder();
        for (int i = 0; i < LongestName; i++)
        {
            byte b = m.ReadU8(at + (uint)i);
            if (b == 0) break;
            text.Append((char)b);
        }
        return text.ToString();
    }

    /// <summary>Forgets what has been said, for a test that must not inherit it.</summary>
    internal static void Forget() => _said.Clear();
}
