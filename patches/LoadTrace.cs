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
    /// How many files have been read, counted whether or not anyone is
    /// tracing. A race is loading for as long as this keeps moving and has
    /// finished when it stops, which is the only cheap way to tell one from
    /// the other from outside the game.
    /// </summary>
    public static int Reads => _reads;

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
        _reads++;
        if (!Tracing) return;

        if (c.A0 == _lastIndex && c.RA == _lastCaller) return;
        _lastIndex = c.A0;
        _lastCaller = c.RA;

        Console.Error.WriteLine($"[load] {_reads,4}: file index {c.A0} asked for from 0x{c.RA:X8}");
        Request(c, m);
    }

    /// <summary>Where func_80016640 asks for the two halves of a car object.</summary>
    const uint AsksForTheModel = 0x80016770u;
    const uint AsksForTheTextures = 0x80016828u;

    /// <summary>
    /// Reports the request record behind a car-object read.
    ///
    /// Loading a car is not a call but a queue: func_80016640 is a state
    /// machine over a request record, stepping through it one file at a time,
    /// and the record says which file and where to put it. A pre-hook runs
    /// before the callee's prologue spills anything, so the caller's registers
    /// are still live here - S1 is the record, S3 the object that owns it, S2
    /// the destination the inflate call is handed.
    ///
    /// The record's file index is the one thing already understood: it is the
    /// archive's own number for carobj/&lt;code&gt;.cdo.gz, and the .cdp is that
    /// number plus one. What a cold launch still needs is the rest - where the
    /// record lives, who owns it, and where the bytes are meant to land.
    /// </summary>
    static void Request(CpuContext c, IMemory m)
    {
        if (c.RA is not (AsksForTheModel or AsksForTheTextures)) return;

        Console.Error.WriteLine(
            $"[load]       request at 0x{c.S1:X8}, owned by 0x{c.S3:X8}, unpacking into 0x{c.S2:X8}"
            + $"{Environment.NewLine}[load]       +0x04 index {m.ReadU16(c.S1 + 0x04u)}"
            + $"  +0x10 step {m.ReadU8(c.S1 + 0x10u)}"
            + $"  +0x48 model -> 0x{m.ReadU32(c.S1 + 0x48u):X8}"
            + $"  +0x4C textures -> 0x{m.ReadU32(c.S1 + 0x4Cu):X8}");
    }
}
