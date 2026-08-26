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

    /// <summary>gt2_01, the overlay that runs a race.</summary>
    const uint RaceOverlayEntry = 0x80011F64u;

    public static void ActivateFromEntry(CpuContext c, IMemory m)
    {
        uint entry = c.A1;

        // Simulation mode opens the multiplayer lobby first. When the lobby
        // finishes, the overlay loads exactly as it always has and the game
        // carries on unaware. The pre-hook cannot skip the load below: it
        // only marks which overlay is arriving, so the load always follows.
        // The lobby can redirect this load, so read the entry point back out
        // afterwards rather than trusting the one that arrived.
        Multiplayer.ModeHook.TryEnterLobby(c, m, entry);
        entry = c.A1;

        // The race overlay arriving is the moment the race is settled: the menu
        // has stopped rewriting the block and nothing has read it yet.
        if (entry == RaceOverlayEntry)
        {
            Multiplayer.RaceClock.Begin();
            Multiplayer.ModeHook.ApplyRaceGrid(m);
        }

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
