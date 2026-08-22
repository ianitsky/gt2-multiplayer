using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace GT2Port;

/// <summary>
/// Selects the overlay whose code is about to be decompressed into RAM.
///
/// The runtime normally picks an overlay from the LBA the drive reads, but GT2
/// never reads its overlay container as a plain file: gt2_load_overlay pulls a
/// gzip blob through the VOL index and inflates it itself, so no LBA ever
/// identifies which overlay is arriving. What does identify it is the entry
/// point the caller passes in A1, taken from the table at 0x80091174.
///
/// Marking the overlay here leaves the runtime's existing rule to finish the
/// job: Dispatcher activates a pending overlay once the game writes into the
/// region it loads at.
/// </summary>
public static class OverlayHook
{
    static readonly Dictionary<uint, string> ByEntryPoint = new()
    {
        [0x80012254u] = "gt2_01", // gt2_ovr1_load_global_menu_overlay
        [0x80011384u] = "gt2_02", // gt2_ovr2_entrypoint0
        [0x80011750u] = "gt2_03", // gt2_ovr3_entrypoint0
    };

    static readonly HashSet<uint> Unknown = new();

    public static void ActivateFromEntry(CpuContext c, IMemory m)
    {
        uint entry = c.A1;
        if (ByEntryPoint.TryGetValue(entry, out var name))
        {
            Dispatcher.Load(name);
            return;
        }

        // Worth surfacing rather than silently loading nothing: it means an
        // overlay is being loaded that this table does not know about yet.
        if (Unknown.Add(entry))
            Console.WriteLine($"[Overlay] no overlay mapped to entry point 0x{entry:X8}");
    }
}
