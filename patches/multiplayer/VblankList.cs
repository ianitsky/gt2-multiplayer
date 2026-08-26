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

    static bool _said;

    static string Where(uint address) => address switch
    {
        >= 0x801F0000u and < 0x80200000u => "the main stack",
        >= 0x80010000u and < 0x80060000u => "overlay code",
        >= 0x80000000u and < 0x80200000u => "data",
        _ => "not RAM at all",
    };

    static bool IsCode(uint address) => address is >= 0x80000000u and < 0x80200000u;

    /// <summary>Pre-hook on the walker. Reads the list; changes nothing.</summary>
    public static void AboutToRun(CpuContext c, IMemory m)
    {
        if (!Watching || _said) return;

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

        if (!bad) return;
        _said = true;
        Console.Error.WriteLine($"[vblank] the callback list from 0x{head:X8} holds something that is not code:");
        foreach (var line in lines) Console.Error.WriteLine(line);
    }
}
