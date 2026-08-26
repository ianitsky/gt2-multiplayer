using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Puts the arcade's setup steps in order.
///
/// A launch that skips the arcade's screens keeps whatever ran before them and
/// loses whatever ran as part of them. Everything else about the direct launch
/// is settled; what is not is which side of that line the car loader's owner
/// falls on. The code that installs the two request records lives in
/// func_80013BE4, which nothing calls by address - it is reached through a
/// pointer - so reading cannot answer it and watching can.
///
/// Each step says when it happened and how many came before, so the answer is
/// the order the lines come out in rather than anything that has to be
/// interpreted: if the owner is known before the car and track screen is
/// constructed, skipping that screen keeps it.
///
/// Off unless GT2_ARCADE_ORDER is set.
/// </summary>
public static class ArcadeOrder
{
    static readonly bool Watching =
        Environment.GetEnvironmentVariable("GT2_ARCADE_ORDER") is not (null or "");

    static int _step;
    static readonly HashSet<string> _said = [];

    /// <summary>
    /// Notes one step, the first time it happens. Only the first matters: the
    /// arcade is a loop, and a step that repeats every visit to a menu would
    /// bury the one ordering that is being asked about.
    /// </summary>
    static void Note(string what)
    {
        if (!Watching || !_said.Add(what)) return;
        Console.Error.WriteLine($"[order] {++_step}. {DateTime.UtcNow:HH:mm:ss.fff}  {what}");
    }

    /// <summary>Pre-hook on the arcade's entry point.</summary>
    public static void ArcadeEntered(CpuContext c, IMemory m) =>
        Note("the arcade overlay's entry point runs");

    /// <summary>Pre-hook on func_80013BE4, which installs the two car request records.</summary>
    public static void OwnerBuilt(CpuContext c, IMemory m) =>
        Note($"func_80013BE4 runs - the one that installs the request records (A0=0x{c.A0:X8})");

    /// <summary>Pre-hook on the car and track screen's constructor.</summary>
    public static void ScreenConstructed(CpuContext c, IMemory m) =>
        Note($"the car and track screen is constructed (object at 0x{c.A0:X8})");

    /// <summary>Pre-hook on the builder of the 720-byte race parameters block.</summary>
    public static void ParametersBuilt(CpuContext c, IMemory m) =>
        Note("the 720-byte race parameters block is built");

    /// <summary>
    /// Called by CarLoad the first time the loader is ticked, which is the
    /// first moment the owner is known to the port at all.
    /// </summary>
    public static void OwnerKnown(uint owner) =>
        Note($"the car loader ticks for the first time - owner 0x{owner:X8}");

    /// <summary>Called when an overlay is about to load, to close the sequence.</summary>
    public static void RaceLoading() => Note("the race overlay is loaded");
}
