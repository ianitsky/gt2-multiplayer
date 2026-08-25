using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port;

/// <summary>
/// Lists the files a race needs, in the order the game asks for them.
///
/// Starting a race by supplying the block alone does not work: the race
/// overlay runs and then reads through an object nobody built. So the block
/// describes the race but does not prepare it, and what the arcade does
/// between filling one and loading the other is the thing to find out.
///
/// Every file the game reads passes through
/// gt2_main_vol_get_file_data_sector_offset with its index in A0, and
/// tools/vol_index.py turns those indices back into names. Watching a race
/// that works therefore gives the list a race that is launched cold would have
/// to load for itself.
///
/// Off unless GT2_LOAD_TRACE is set.
/// </summary>
public static class LoadTrace
{
    static readonly bool Tracing =
        Environment.GetEnvironmentVariable("GT2_LOAD_TRACE") is not (null or "");

    static int _reads;

    /// <summary>Pre-hook on gt2_main_vol_get_file_data_sector_offset.</summary>
    public static void Reading(CpuContext c, IMemory m)
    {
        if (!Tracing) return;
        Console.Error.WriteLine($"[load] {++_reads,4}: file index {c.A0}");
    }
}
