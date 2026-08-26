using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Finds the record the race reads its driver's controls out of.
///
/// The plumbing is settled by reading. PadInitDirect is called once from
/// 0x800109EC with the two raw buffers, 0x801F0C98 and 0x801F0CBA, and the
/// pair sits inside a struct at 0x801F0C70. A VBlank callback installed at
/// 0x8007F924 runs 0x8007F978 every frame, which detects each pad's presence
/// and then hands each one to 0x8007FC30 - the decoder - along with a reader
/// record the game keeps at [0x801F0C80] and [0x801F0C84].
///
/// The decoder computes the raw buffer as padIndex * 34 + 0x801F0C98, reads
/// the connection byte, the type nibble and the sixteen button bits, and
/// writes the result into the reader record. Any screen that wants input
/// registers a record through 0x8007FAB8 with the pad it wants.
///
/// What reading has not settled is which record the race uses and what reads
/// it afterwards, and that is what a launched race has to know before a remote
/// player's car can be driven from the wire. Static reading has run out here -
/// sixty-six functions in gt2_01 branch on the race's mode byte alone - so
/// this asks the running game instead.
///
/// Off unless GT2_PAD_WATCH is set.
/// </summary>
public static class PadWatch
{
    static readonly bool Watching =
        Environment.GetEnvironmentVariable("GT2_PAD_WATCH") is not (null or "");

    /// <summary>The struct holding both pads and the two reader records.</summary>
    const uint PadStruct = 0x801F0C70u;

    /// <summary>Where the raw buffer for pad 0 begins, and how far apart they are.</summary>
    const uint FirstRawBuffer = 0x801F0C98u;
    const int RawBufferStride = 34;

    /// <summary>Where a reader record keeps the pad it was registered for.</summary>
    const uint PadIndexIn = 0x00u;

    static readonly HashSet<uint> _registered = [];

    /// <summary>
    /// Pre-hook on the registration: A0 the record, A1 the pad, A2 whatever the
    /// caller wants handed back. Said once per record, because a screen that
    /// re-registers every frame would bury the one that matters.
    /// </summary>
    public static void ReaderRegistered(CpuContext c, IMemory m)
    {
        if (!Watching || !_registered.Add(c.A0)) return;

        Console.Error.WriteLine(
            $"[pad] a reader for pad {(short)c.A1} is registered at 0x{c.A0:X8}"
            + $" (callback 0x{c.A2:X8})");
    }

    /// <summary>How often to report a decode, in frames, once one has been seen.</summary>
    const int Every = 120;

    static int _decodes;

    /// <summary>
    /// Pre-hook on the decoder: A0 is the reader record about to be filled.
    ///
    /// Reported rarely and with the raw bytes beside the record, so a run says
    /// both which record the race is filling and that the bytes reaching it are
    /// the ones the host is sending.
    /// </summary>
    public static void PadDecoded(CpuContext c, IMemory m)
    {
        if (!Watching || c.A0 == 0u) return;
        if (_decodes++ % Every != 0) return;

        uint record = c.A0;
        int pad = (short)m.ReadU16(record + PadIndexIn);
        uint raw = FirstRawBuffer + (uint)(pad * RawBufferStride);

        Console.Error.WriteLine(
            $"[pad] record 0x{record:X8} pad {pad}:"
            + $" connected={m.ReadU8(raw)} type=0x{m.ReadU8(raw + 1u):X2}"
            + $" buttons=0x{m.ReadU8(raw + 3u) << 8 | m.ReadU8(raw + 2u):X4}"
            + $" sticks {m.ReadU8(raw + 4u):X2} {m.ReadU8(raw + 5u):X2}"
            + $" {m.ReadU8(raw + 6u):X2} {m.ReadU8(raw + 7u):X2}");
    }

    /// <summary>The records the game has registered, for a caller that wants them.</summary>
    public static IReadOnlyCollection<uint> Records => _registered;

    /// <summary>Where the game keeps the record for <paramref name="pad"/>.</summary>
    public static uint RecordFor(IMemory m, int pad) =>
        m.ReadU32(PadStruct + 0x10u + (uint)(pad * 4));
}
