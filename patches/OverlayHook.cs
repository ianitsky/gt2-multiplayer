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
public static partial class OverlayHook
{

    static readonly HashSet<uint> Unknown = new();

    public static void ActivateFromEntry(CpuContext c, IMemory m)
    {
        uint entry = c.A1;

        // Simulation mode is where multiplayer lives now. The lobby runs to
        // completion here; the overlay is only loaded if it declines.
        if (Multiplayer.ModeHook.TryEnterLobby(entry)) return;

        if (ByEntryPoint.TryGetValue(entry, out var name))
        {
            Console.WriteLine($"[Overlay] load {name} (entry 0x{entry:X8})");
            Dispatcher.Load(name);
            return;
        }

        // Worth surfacing rather than silently loading nothing: it means an
        // overlay is being loaded that this table does not know about yet.
        if (Unknown.Add(entry))
            Console.WriteLine($"[Overlay] no overlay mapped to entry point 0x{entry:X8}");
    }
}
