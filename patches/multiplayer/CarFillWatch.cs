using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Reports how the arcade calls the thing that puts a car into a race.
///
/// func_80010554 is what a walked race goes through: it takes the car's record
/// and, among other things, calls load_car_parts with the entrant as its
/// destination. Calling load_car_parts directly instead gave a car with no
/// engine and no tyres, so the parts come from something else func_80010554
/// does around it.
///
/// Replicating that call means knowing its arguments, and it has seven - four
/// in registers and three on the stack, against a frame of 0xA0. Two of them
/// are a register the builder holds and a buffer it points at, neither of which
/// reading the code has settled. Watching one walked race settles both.
///
/// Off unless GT2_CAR_FILL is set.
/// </summary>
public static class CarFillWatch
{
    static readonly bool Watching =
        Environment.GetEnvironmentVariable("GT2_CAR_FILL") is not (null or "");

    /// <summary>How many calls to report before the shape is clear.</summary>
    const int MostReports = 8;

    static int _reported;

    /// <summary>
    /// Pre-hook on func_80010554. The stack arguments sit above the caller's
    /// own frame, where the caller left them - the callee reaches them at
    /// SP+0xAC and up once it has claimed its 0xA0.
    /// </summary>
    public static void Filling(CpuContext c, IMemory m)
    {
        if (!Watching || _reported++ >= MostReports) return;

        Console.Error.WriteLine(
            $"[fill] {_reported}. func_80010554("
            + $"A0=0x{c.A0:X8} A1=0x{c.A1:X8} A2=0x{c.A2:X8} A3=0x{c.A3:X8}"
            + $" | +0x10=0x{m.ReadU32(c.SP + 0x10u):X8}"
            + $" +0x14=0x{m.ReadU32(c.SP + 0x14u):X8}"
            + $" +0x18=0x{m.ReadU32(c.SP + 0x18u):X8})");
    }

    /// <summary>Pre-hook on load_car_parts, to see which entrant it is aimed at.</summary>
    public static void LoadingParts(CpuContext c, IMemory m)
    {
        if (!Watching || _reported >= MostReports) return;

        Console.Error.WriteLine(
            $"[fill]     load_car_parts(record=0x{c.A0:X8}, into=0x{c.A1:X8})");
    }
}
