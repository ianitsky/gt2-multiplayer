using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Finds out what the arcade's screens leave behind.
///
/// The arcade's race case builds an object at 0x801C3350, runs a screen over
/// it, and then copies 0x2D0 bytes of it out to 0x801D5FA0 on its way into the
/// race. A launch that skips the screen would have the object as built and
/// nothing more - so the question that decides whether skipping is possible at
/// all is: how much of those 720 bytes does the screen fill in?
///
/// Two captures answer it. One the moment the object is built, before any
/// screen has run; one the moment the race overlay is loaded, after they have.
/// The bytes that differ are exactly what a direct launch would have to supply
/// for itself, and the bytes that do not are what it gets for free.
///
/// Off unless GT2_ARCADE_SETUP is set.
/// </summary>
public static class ArcadeSetupWatch
{
    /// <summary>The object the arcade's race case builds and the screen fills.</summary>
    const uint Built = 0x801C3350u;

    /// <summary>How much of it the arcade copies out on its way into the race.</summary>
    const int Copied = 0x2D0;

    /// <summary>Where those bytes are copied to.</summary>
    const uint CopiedTo = 0x801D5FA0u;

    /// <summary>gt2_01's entry, which is what makes an overlay load the race.</summary>
    const uint RaceOverlayEntry = 0x80011F64u;

    static readonly bool Watching =
        Environment.GetEnvironmentVariable("GT2_ARCADE_SETUP") is not (null or "");

    static int _seen;

    /// <summary>
    /// Post-hook on 0x80010C84, the one call that builds the object. Runs
    /// after it returns, so what it captures is the object as the arcade
    /// leaves it before any screen touches it.
    /// </summary>
    public static void ObjectBuilt(CpuContext c, IMemory m)
    {
        if (!Watching) return;
        Dump(m, Built, Copied, "arcade-as-built.bin", "as built, before any screen");
    }

    /// <summary>
    /// Pre-hook on gt2_load_overlay. Only the race matters here, and only the
    /// first one: a second race would capture a screen's leftovers rather than
    /// a screen's work.
    /// </summary>
    public static void LoadingOverlay(CpuContext c, IMemory m)
    {
        if (c.A1 == RaceOverlayEntry) ArcadeOrder.RaceLoading();
        if (!Watching || c.A1 != RaceOverlayEntry || _seen++ > 0) return;

        Dump(m, Built, Copied, "arcade-as-raced.bin", "as raced, after the screens");
        Dump(m, CopiedTo, Copied, "arcade-copied-to.bin", "where the arcade copies it");
    }

    static void Dump(IMemory m, uint at, int length, string path, string what)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++) bytes[i] = m.ReadU8(at + (uint)i);

        int set = bytes.Count(b => b != 0);
        try
        {
            File.WriteAllBytes(path, bytes);
            Console.Error.WriteLine(
                $"[setup] 0x{at:X8} {what}: {length} bytes to {path}, {set} of them non-zero");
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"[setup] could not write {path}: {e.Message}");
        }
    }
}
