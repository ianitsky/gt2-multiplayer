using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Interrupts;
using RecompOne.Runtime.Memory;
using BiosKernel = RecompOne.Runtime.Bios.Bios;

// This replaces the generated Entry.cs (excluded from the build). RecompOne's
// EntryWriter is not handed the SYSTEM.CNF, so it emits SP straight from the
// PS-EXE header. GT2's header carries SP=0 and expects the BIOS to apply
// SYSTEM.CNF's STACK instead; booting with SP=0 makes start() write to
// 0xFFFFFFF8 on its first stack push.
const uint Stack = 0x801FFF00;   // SYSTEM.CNF STACK
const uint EntryPC = 0x8005D600; // PS-EXE initial PC
const uint LoadAddr = 0x80010000;
const int ExeOffset = 0x800;
const int ExeSize = 626688;
const string BootExe = "SCUS_944.88";

var m = new PSMemory();

RecompOne.Runtime.Runtime.Initialize("Gran Turismo 2");
RecompOne.Runtime.Runtime.WaitForValidDisc();

using var fs = DiscFs.Open(RecompOne.Runtime.Runtime.CdPath);
var cd = new CdController(fs, m);
m.SetCd(cd);

Dispatcher.Register("main", new Recompiled.MainDispatchTable());
Dispatcher.Register("gt2_01", new Recompiled.Gt2_01DispatchTable());
Dispatcher.Register("gt2_02", new Recompiled.Gt2_02DispatchTable());
Dispatcher.Register("gt2_03", new Recompiled.Gt2_03DispatchTable());
Dispatcher.Register("gt2_04", new Recompiled.Gt2_04DispatchTable());
Dispatcher.Register("gt2_05", new Recompiled.Gt2_05DispatchTable());
Dispatcher.Register("gt2_06", new Recompiled.Gt2_06DispatchTable());
RecompOne.Runtime.Modding.ModLoader.LoadAll();
GT2Port.RuntimePatches.Load("patches/runtime");
RecompOne.Runtime.Runtime.FramePatch = m =>
{
    GT2Port.RuntimePatches.Apply(m);
    GT2Port.RaceWatch.Tick(m);
    GT2Port.Multiplayer.RaceClock.Tick();
};

cd.LoadToMemory(BootExe, LoadAddr, ExeOffset, ExeSize);
Dispatcher.Load("main");

// Interrupt delivery: the runtime's existing IRQ path runs the handler the game
// registered through the BIOS, the host pump keeps the window alive while the
// game sits in a busy-wait, and the wall clock decides when a VBlank happens.
Irq.Deliver = RecompOne.Runtime.Runtime.DispatchIrq;
Irq.PumpHost = RecompOne.Runtime.Runtime.PumpHost;
// The window belongs to this thread, and the game's tasks run on threads of
// their own, so this one has to keep answering the desktop while it waits.
RecompOne.Runtime.Dispatch.TaskStacks.PumpWhileParked = RecompOne.Runtime.Runtime.PumpHost;

var vblank = new WallClockVBlankSource();
vblank.Start(() => InterruptController.Raise());

var c = new CpuContext();
c.GP = 0u;
c.SP = Stack;
c.FP = c.SP;
c.RA = 0u;

RecompOne.Runtime.Runtime.SetContext(c, m);
BiosKernel.Init(m);

// A crash that reaches the console can be lost - a closed window, a swallowed
// stream - and this port has already wasted time on a death that looked
// silent. Everything that kills the process gets written down.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var text = $"{DateTime.Now:u}{Environment.NewLine}{e.ExceptionObject}{Environment.NewLine}";
    try { File.AppendAllText("crash.log", text); } catch { }
    Console.Error.WriteLine(text);
    Console.Error.Flush();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    // Fires on a deliberate exit and not on a stack overflow or a native
    // crash, which is exactly the distinction a silent death needs.
    try { File.AppendAllText("crash.log", $"{DateTime.Now:u} process exit{Environment.NewLine}"); } catch { }
};
AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
{
    // Not a crash on its own - the setjmp path throws by design - but the last
    // one before a silent exit is usually the one that mattered.
    if (e.Exception is RecompOne.Runtime.Dispatch.LongJmpSignal) return;
    try { File.AppendAllText("crash.log", $"{DateTime.Now:u} first-chance {e.Exception.GetType().Name}: {e.Exception.Message}{Environment.NewLine}"); } catch { }
};
// longjmp discards the frames between it and its setjmp, so it unwinds to
// here and execution resumes at the RA the jmp_buf restored.
uint resumeAt = EntryPC;
while (true)
{
    try
    {
        Dispatcher.Call(c, m, resumeAt);

        // A resume point returning is not the end of the game. longjmp is
        // modelled as an unwind to here, so the frames below the matching
        // setjmp are gone, and the continuation returns with nowhere to go -
        // on hardware it would return into the setjmp's caller. That address
        // is still in RA, so carry on there instead of falling out of the
        // game. An RA the recompiler never emitted an entry for surfaces as
        // "unmapped call" naming the address, which is how this port has
        // always found the ones the sweep missed.
        if (c.RA == 0u || c.RA == resumeAt) break;
        Console.Error.WriteLine($"[Boot] resume point returned; continuing at 0x{c.RA:X8}");
        resumeAt = c.RA;
        continue;
    }
    catch (RecompOne.Runtime.Dispatch.LongJmpSignal)
    {
        resumeAt = c.RA;
    }
}

// The game's entry never returns on hardware: it drives the console until the
// power goes off. Reaching here means the recompiled call stack unwound, which
// looks from outside like the process quietly closing.
{
    var text = "[Boot] the game's entry point returned - the call stack unwound out of the game";
    Console.Error.WriteLine(text);
    try { File.AppendAllText("crash.log", $"{DateTime.Now:u} {text}{Environment.NewLine}"); } catch { }
}
