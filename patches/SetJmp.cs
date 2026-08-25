using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port;

/// <summary>
/// Raised by longjmp to unwind back to the matching setjmp.
///
/// The recompiler turns the game's longjmp into an ordinary C# return, which
/// is wrong: the routine overwrites RA from the jmp_buf, so on hardware its
/// final `jr ra` lands wherever setjmp was called, never back in its caller.
/// gt2_load_overlay relies on exactly that - it loads an overlay and jumps
/// back into gt2_main_task1 rather than returning - and letting it return
/// carried on through code that was never meant to run again, with registers
/// belonging to another context.
///
/// Discarding the intervening frames is what longjmp does, so an exception
/// models it directly. Program.cs catches this at the root and resumes at the
/// restored RA.
/// </summary>
/// <summary>setjmp/longjmp, which GT2 uses to restart its main loop after loading an overlay.</summary>
public static class SetJmp
{
    /// <summary>
    /// setjmp(env) - gt2_main_saveregisters at 0x8007AD58, which runs as normal
    /// after this. Filling a jmp_buf is also what decides where a later longjmp
    /// has to stop, so the dispatcher notes how deep the call stack was here:
    /// the frame that called setjmp is the frame the unwind must not pass.
    /// </summary>
    public static void SaveJmp(CpuContext c, IMemory m)
    {
        RecompOne.Runtime.Dispatch.Dispatcher.RecordSetJmp(c.A0);
    }

    /// <summary>
    /// longjmp(env, value) — gt2_main_task201_reload_regs at 0x8007AD90.
    /// Restores the callee-saved set the matching setjmp stored, then unwinds.
    /// </summary>
    public static void LongJmp(CpuContext c, IMemory m)
    {
        uint env = c.A0;
        uint value = c.A1;

        // The value is the game's own reason for giving up, and the resume
        // point switches on it. Anything the resume point does not recognise
        // falls straight through and returns - which, with this model, means
        // returning out of the game entirely. Worth naming when it happens.
        var (recorded, now) = RecompOne.Runtime.Dispatch.Dispatcher.Depths(env);
        Console.Error.WriteLine(
            $"[longjmp] env=0x{env:X8} value={value} resuming at 0x{m.ReadU32(env):X8}"
            + $" (setjmp depth {recorded}, now {now})");

        c.RA = m.ReadU32(env);
        c.SP = m.ReadU32(env + 0x04u);
        c.FP = m.ReadU32(env + 0x08u);
        c.S0 = m.ReadU32(env + 0x0Cu);
        c.S1 = m.ReadU32(env + 0x10u);
        c.S2 = m.ReadU32(env + 0x14u);
        c.S3 = m.ReadU32(env + 0x18u);
        c.S4 = m.ReadU32(env + 0x1Cu);
        c.S5 = m.ReadU32(env + 0x20u);
        c.S6 = m.ReadU32(env + 0x24u);
        c.S7 = m.ReadU32(env + 0x28u);
        c.GP = m.ReadU32(env + 0x2Cu);

        c.V0 = value;
        throw new RecompOne.Runtime.Dispatch.LongJmpSignal(c.RA, env, RecompOne.Runtime.Dispatch.Dispatcher.CurrentDepth);
    }
}
