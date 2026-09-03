using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Reports the VBlank callback list when one of its entries is not code.
///
/// The handler walks a linked list - head at [A0], next at +0x04, the callback
/// at +0x08 - and calls each entry in turn. A launched race dies in it, calling
/// 0x003C00A0, which is not an address at all: it reads like two sixteen-bit
/// values sitting where a pointer should be. So a node was registered and its
/// memory reused underneath the list, and what matters is which node and where
/// it lives - the stack, an overlay, or something allocated - because that says
/// who left it behind.
///
/// Reading the list rather than guessing is the point. Off unless
/// GT2_VBLANK_LIST is set.
/// </summary>
public static class VblankList
{
    static readonly bool Watching =
        Environment.GetEnvironmentVariable("GT2_VBLANK_LIST") is not (null or "");

    /// <summary>Longest list worth following before calling it circular.</summary>
    const int MostNodes = 32;

    /// <summary>How many changes are worth printing before the point is made.</summary>
    const int MostReports = 40;

    /// <summary>The node the register/unregister pair at 0x800686C8 owns.</summary>
    const uint TheNode = 0x801C949Cu;

    /// <summary>Pre-hook on the register. Says when, and what the node holds.</summary>
    public static void Registered(CpuContext c, IMemory m) => Say(m, "registered");

    /// <summary>Pre-hook on the unregister.</summary>
    public static void Unregistered(CpuContext c, IMemory m) => Say(m, "unregistered");

    static void Say(IMemory m, string what)
    {
        if (!Watching) return;
        Console.Error.WriteLine(
            $"[vblank] node 0x{TheNode:X8} {what}"
            + $" (next 0x{m.ReadU32(TheNode + 0x4u):X8}, calls 0x{m.ReadU32(TheNode + 0x8u):X8})");
    }

    /// <summary>Kept so the caller that marked the comparison moment still builds.</summary>
    public static void ReportOnce() { }

    static string Where(uint address) => address switch
    {
        >= 0x801F0000u and < 0x80200000u => "the main stack",
        >= 0x80010000u and < 0x80060000u => "overlay code",
        >= 0x80000000u and < 0x80200000u => "data",
        _ => "not RAM at all",
    };

    static bool IsCode(uint address) => address is >= 0x80000000u and < 0x80200000u;

    /// <summary>What the list looked like last time, so only changes are said.</summary>
    static string _last = "";

    static int _changes;

    /// <summary>
    /// Pre-hook on the walker. Reads the list; changes nothing.
    ///
    /// Reported whenever it changes rather than once, because one photograph
    /// settles nothing: taken where the arcade builds its parameters, a walked
    /// run and a launched one hold exactly the same five nodes. Whatever goes
    /// wrong goes wrong after that, so what is needed is the sequence either
    /// side of it and the point where the two stop agreeing.
    /// </summary>
    public static void AboutToRun(CpuContext c, IMemory m)
    {
        // Borrowed as a frame tick. This hook runs the console's vblank list,
        // so it is the one place already called every frame on every screen -
        // including the title menu, which no race hook ever sees. VramDump
        // returns immediately unless it has been asked for.
        VramDump.Tick();
        TitleLabel.Tick();

        if (!Watching) return;

        uint head = c.A0;
        var lines = new List<string>();
        bool bad = false;

        uint node = m.ReadU32(head);
        for (int i = 0; i < MostNodes && node != 0u; i++)
        {
            if (!IsCode(node)) { lines.Add($"[vblank]   node 0x{node:X8} is {Where(node)}"); bad = true; break; }

            uint next = m.ReadU32(node + 0x4u);
            uint callback = m.ReadU32(node + 0x8u);
            bool ok = IsCode(callback);
            if (!ok) bad = true;

            lines.Add($"[vblank]   node 0x{node:X8} in {Where(node),-13} calls 0x{callback:X8}"
                      + (ok ? "" : $"  <-- {Where(callback)}"));
            node = next;
        }

        string now = string.Join("|", lines);
        if (now == _last) return;
        _last = now;

        // A list that keeps changing is a different problem from one that
        // changes once and breaks, and a cap keeps the first from burying the
        // second under thousands of lines.
        if (++_changes > MostReports) return;

        Console.Error.WriteLine($"[vblank] {_changes}. the callback list from 0x{head:X8}"
            + (bad ? " holds something that is not code:" : ":"));
        foreach (var line in lines) Console.Error.WriteLine(line);
    }
}
