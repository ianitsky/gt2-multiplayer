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
RecompOne.Runtime.Modding.ModLoader.LoadAll();

cd.LoadToMemory(BootExe, LoadAddr, ExeOffset, ExeSize);
Dispatcher.Load("main");

// Interrupt delivery: the runtime's existing IRQ path runs the handler the game
// registered through the BIOS, the host pump keeps the window alive while the
// game sits in a busy-wait, and the wall clock decides when a VBlank happens.
Irq.Deliver = RecompOne.Runtime.Runtime.DispatchIrq;
Irq.PumpHost = RecompOne.Runtime.Runtime.PumpHost;

var vblank = new WallClockVBlankSource();
vblank.Start(() => InterruptController.Raise());

var c = new CpuContext();
c.GP = 0u;
c.SP = Stack;
c.FP = c.SP;
c.RA = 0u;

RecompOne.Runtime.Runtime.SetContext(c, m);
BiosKernel.Init(m);
Dispatcher.Call(c, m, EntryPC);
