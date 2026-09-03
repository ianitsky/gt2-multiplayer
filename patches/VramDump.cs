using RecompOne.Runtime;
using RecompOne.Runtime.Hle;

namespace GT2Port;

/// <summary>
/// Writes the console's video memory to a file, so a picture the game put on
/// the screen can be read offline.
///
/// The title menu's labels live in arcade/title_item.tim.gz, a 4bpp sheet with
/// no palette of its own: the CLUT is somewhere in video memory, and which one
/// a label is drawn with is not written down anywhere the disc can be asked.
/// That matters for replacing one, because a glyph borrowed from a label drawn
/// with a different palette is the right shape in the wrong colours - and the
/// only way to know is to look at the memory the game is actually drawing
/// from.
///
/// Off unless RECOMPONE_VRAM_DUMP is set. Its value is how many pictures to
/// take - a megabyte a frame would be a file nobody can open and a frame rate
/// nobody can play at, so they are spaced out and counted.
///
/// More than one because the interesting screen cannot be asked for. The
/// first attempt at this took a single picture a second after boot and caught
/// the licence screen; the title menu is a boot sequence away and how long
/// that takes is the disc's business, not something to guess at. A handful of
/// pictures a few seconds apart catches it without anyone having to time it.
/// </summary>
public static class VramDump
{
    static readonly bool Wanted =
        Environment.GetEnvironmentVariable("RECOMPONE_VRAM_DUMP") is not (null or "");

    /// <summary>How long after the first tick to take the first picture.</summary>
    static readonly TimeSpan Settle = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("RECOMPONE_VRAM_DUMP_AFTER"), out int s)
            ? Math.Clamp(s, 0, 600)
            : 1);

    /// <summary>How long between them.</summary>
    static readonly TimeSpan Apart = TimeSpan.FromSeconds(
        int.TryParse(Environment.GetEnvironmentVariable("RECOMPONE_VRAM_DUMP_EVERY"), out int e)
            ? Math.Clamp(e, 1, 600)
            : 3);

    /// <summary>How many to take. The switch's own value, or one.</summary>
    static readonly int HowMany =
        int.TryParse(Environment.GetEnvironmentVariable("RECOMPONE_VRAM_DUMP"), out int n)
            ? Math.Clamp(n, 1, 99)
            : 1;

    static DateTime _first;
    static DateTime _last;
    static int _taken;

    /// <summary>
    /// Called every frame. Does nothing at all unless asked, which is why it
    /// can sit on a hook that runs at the console's frame rate.
    /// </summary>
    public static void Tick()
    {
        if (!Wanted || _taken >= HowMany) return;

        var now = DateTime.UtcNow;
        if (_first == default) { _first = now; return; }
        if (now - _first < Settle) return;
        if (_taken > 0 && now - _last < Apart) return;

        if (Runtime.Gpu is not { } gpu) return;

        _taken++;
        _last = now;

        var pixels = new ushort[Gpu.VramWidth * Gpu.VramHeight];

        // The hardware backend keeps its own copy and the software shadow is
        // not it, so the backend is asked first and the shadow is the fallback
        // for a headless or failed-init run.
        if (GpuHle.Backend is { Ready: true } backend)
            backend.ReadVram(0, 0, Gpu.VramWidth, Gpu.VramHeight, pixels);
        else
            Array.Copy(gpu.Vram, pixels, pixels.Length);

        var bytes = new byte[pixels.Length * 2];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);

        string path = Path.Combine(AppContext.BaseDirectory, $"vram-{_taken:00}.bin");
        File.WriteAllBytes(path, bytes);

        Console.Error.WriteLine(
            $"[vram] {_taken} of {HowMany}: {Gpu.VramWidth}x{Gpu.VramHeight} written to {path}"
            + $" at {(now - _first).TotalSeconds:F1}s");
    }
}
