using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Writes the installed race record to a file, so two races can be compared
/// byte for byte.
///
/// A viewer is meant to see what the attract demo sees, and the demo's race is
/// not the arcade's with one byte changed - writing 2 into the kind at +0x0A of
/// an arcade race loaded it and then walked straight back out to the menu. The
/// rest of the record is shaped differently and the only way to know how is to
/// hold the two side by side.
///
/// The demo builds its own record: gt2_02's menu task reads a blob at
/// 0x800E15C0, has func_80020E14 build 1420 bytes of race into 0x801055C0 from
/// an index it cycles, and installs that at 0x801D585C. Our own race is built
/// by the arcade instead. Both end up in the same place, which is what makes
/// them comparable.
///
/// GT2_DUMP_RACE names a file. The dump is taken at the race's first frame,
/// which is late enough that whatever built the record has finished with it and
/// early enough that the race has not begun changing it.
/// </summary>
public static class RaceBlockDump
{
    /// <summary>Where a race is installed, and how much of it is the record.</summary>
    const uint Block = 0x801D585Cu;
    const int Size = 0x58C;

    /// <summary>
    /// The buffer a race is assembled in before it is installed, and enough of
    /// it to hold what the installer reads.
    ///
    /// gt2_main_func21 copies 0x58C bytes from here into the block, then reads
    /// the kind byte at +0x0A of what it just copied and, for every kind but
    /// zero, copies a further 0x1C0 from the source that follows. So the record
    /// is not 0x58C bytes - it is 0x58C and then some, and how much depends on
    /// what kind of race it is. Taking 0x800 covers both shapes with room over.
    ///
    /// This is what makes a demo's race reproducible: the buffer is the
    /// installer's own input, and the installer lives in main, so it is still
    /// there while the race overlay is loaded.
    /// </summary>
    const uint Source = 0x801055C0u;
    const int SourceSize = 0x800;

    /// <summary>
    /// The head of the race context, up to where the cars begin.
    ///
    /// The record says what race this is; this says how the race is being run.
    /// A demo's replay and an arcade race come up through the same
    /// 12RaceMenuLoop - the port's own first-frame hook is that class's slot
    /// 0x10, and it fires for both - so whatever makes one a replay is state
    /// rather than code, and this is where the race keeps its state.
    ///
    /// 0x800A9688 is the first car and 0x800A9500 is the base the port already
    /// knows; this starts a little below both so the neighbourhood the replay
    /// cameras cheat points at is inside it too.
    /// </summary>
    const uint Context = 0x800A9000u;
    const int ContextSize = 0x700;

    /// <summary>
    /// Which frame to take the dump on.
    ///
    /// Not the first: the port's first-frame hook is a pre-hook on the method
    /// that builds the race screen, so on that pass the cars have not been made
    /// yet and half the context is still whatever the last screen left. Two
    /// frames in, everything the race sets up is set up.
    /// </summary>
    const int OnFrame = 2;

    static int _frames;

    static readonly string Path = Environment.GetEnvironmentVariable("GT2_DUMP_RACE") ?? "";

    static bool _written;

    /// <summary>Called at the race's first frame, whoever built the race.</summary>
    public static void RaceIsRunning(IMemory m)
    {
        if (Path.Length == 0 || _written) return;
        if (_frames++ < OnFrame) return;
        _written = true;

        var bytes = Read(m, Block, Size);
        var source = Read(m, Source, SourceSize);

        if (!TryWrite(Path, bytes)) return;
        TryWrite(Path + ".src", source);
        TryWrite(Path + ".ctx", Read(m, Context, ContextSize));

        Console.Error.WriteLine(
            $"[dump] {Size} bytes of race record -> {Path},"
            + $" {SourceSize} of the buffer it was installed from -> {Path}.src,"
            + $" {ContextSize} of the race context -> {Path}.ctx"
            + $"  (kind {bytes[0x0A]}, {bytes[0x5A]} entrant(s), key \"{Text(bytes, 0x10, 8)}\","
            + $" course \"{Text(bytes, 0x20, 0x20)}\")");
    }

    static byte[] Read(IMemory m, uint at, int size)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[i] = m.ReadU8(at + (uint)i);
        return bytes;
    }

    static bool TryWrite(string path, byte[] bytes)
    {
        try
        {
            File.WriteAllBytes(path, bytes);
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[dump] could not write {path}: {e.Message}");
            return false;
        }
    }

    /// <summary>A NUL-terminated field of the record, for the one line the log gets.</summary>
    static string Text(byte[] bytes, int at, int room)
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < room && at + i < bytes.Length; i++)
        {
            byte b = bytes[at + i];
            if (b == 0) break;
            text.Append(b is >= 32 and <= 126 ? (char)b : '.');
        }
        return text.ToString();
    }
}
