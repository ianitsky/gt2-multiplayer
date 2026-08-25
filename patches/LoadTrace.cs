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
    static uint _lastIndex = uint.MaxValue;
    static uint _lastCaller;

    /// <summary>
    /// Pre-hook on gt2_main_vol_get_file_data_sector_offset.
    ///
    /// The return address matters as much as the index: knowing which file a
    /// race needs is not the same as knowing how to ask for it, and the caller
    /// is where that question is answered. Repeats of the same file from the
    /// same place are collapsed - the selection screen asks for its car logos
    /// nearly two hundred times, and that is not the interesting part.
    /// </summary>
    public static void Reading(CpuContext c, IMemory m)
    {
        if (!Tracing) return;

        _reads++;
        if (c.A0 == _lastIndex && c.RA == _lastCaller) return;
        _lastIndex = c.A0;
        _lastCaller = c.RA;

        Console.Error.WriteLine($"[load] {_reads,4}: file index {c.A0} asked for from 0x{c.RA:X8}");
    }
}
