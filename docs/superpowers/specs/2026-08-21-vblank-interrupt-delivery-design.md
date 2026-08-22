# VBlank interrupt delivery

**Date:** 2026-08-21
**Status:** Approved, not yet implemented

## Problem

The port builds and boots, then hangs on a white window until Windows kills it.

`gt2_sysinit_vsync_setup` (`0x80010954`) registers a VBlank handler and then
busy-waits for it to have run four times:

```
VSyncCallback(gt2_vsync_handler)
L8001096C:
  V0 = m.ReadU32(0x80011DF4)      // vblank counter
  if ((int)V0 < 4) goto L8001096C;
```

`gt2_vsync_handler` (`0x80010928`) is what increments `0x80011DF4`. Nothing ever
calls it, so the counter never moves.

RecompOne's runtime is cooperative and single-threaded: the host only regains
control when the game calls libetc's `VSync`, which is where `PresentFrame`
pumps the window. GT2 does not poll `VSync`; it registers an interrupt callback
and waits. `SdkPatches` reimplements `VSync` but not `VSyncCallback`, and the
runtime has no interrupt delivery at all. The loop never yields, so the window
never pumps events either — hence the white screen.

This is a missing runtime capability, not a defect in our configuration.

## Decisions

| Decision | Choice | Why |
|---|---|---|
| Where it lives | Fork RecompOne (the submodule becomes ours) | Belongs next to `LibEtc`/`Bios`; needs runtime internals. Upstream rejects AI-authored PRs, so it was never going back anyway. |
| Delivery model | Safepoints in loops, on the game thread | No data races, deterministic, mirrors how a real CPU checks interrupts between instructions. Async delivery from another thread would poison the lockstep netcode later. |
| VBlank source | Wall clock behind `IVBlankSource` | Real-time now; the netcode plugs in its own source later and owns frame advance, which lockstep/rollback requires. |
| Scope | VBlank only | Mechanism is generic; only the VBlank source is wired. DMA and timers wait until we have evidence GT2 blocks on them. |

## Architecture

The game stays on the main thread. Only a timer thread is added, and it touches
neither `CpuContext` nor memory — it sets a counter.

```
[timer thread]  every 1/60s -> InterruptController.Raise(VBlank)   // Interlocked
                                       |
[game thread]   ...game loop...
                safepoint (before each backward goto):
                  if (pending != 0 && !masked)
                      Deliver()
                          |- Runtime.PumpHost()        // window pumps events
                          '- Dispatcher.Call(handlerCtx, m, vsyncHandler)
                                 -> gt2_vsync_handler increments 0x80011DF4
                                       |
                busy-wait re-reads 0x80011DF4, advances, exits
```

### Components

| Component | Responsibility |
|---|---|
| `Interrupts.InterruptController` | pending count, `Masked` (interrupt mask state), handler registration, `Deliver()` |
| `Interrupts.IVBlankSource` | pluggable source; `WallClockVBlankSource` is the initial implementation |
| `Sdk.LibApi` | reimplements `VSyncCallback`, `EnterCriticalSection`, `ExitCriticalSection`; registered in `SdkPatches` |
| `FunctionEmmiter` / `InstructionEmitter` | emits the safepoint call before backward branches |
| `Interrupts.Irq` | static facade the generated code calls; `Irq.Poll(c, m)` is the safepoint, kept tiny so it inlines |
| `Runtime.PumpHost()` | new entry point that pumps window events and renders without presenting a game frame, so `Deliver` can unfreeze the window independently of libetc's `VSync` |

Pumping the host from `Deliver` is what unfreezes the window, so the white
screen is fixed by the same change.

### Safepoints

`InstructionEmitter` emits `goto` at four sites (lines 206, 221, 253, 273) and
has `target` at each. A branch is backward when `target <= instr.Vram`, and
every loop closes with a backward branch, so covering those four sites covers
every loop.

```csharp
if (target <= ctrl.Vram) sb.AppendLine($"{indent}Irq.Poll(c, m);");
sb.AppendLine($"{indent}goto L{target:X8};");
```

`Irq.Poll` is a static field read plus a compare. Negligible per iteration but
visible in tight physics loops, so it goes behind a recompiler config flag
(`"safepoints": true`) to make the cost measurable rather than assumed.

### Handler context

Implemented differently than originally planned here. `LegacyInterrupts.Dispatch`
(`RecompOne.Runtime/Hardware/Interrupts.cs`) runs the handler on the game's own
live `CpuContext`, not a fresh isolated one:

```csharp
var snap = cpu.Snapshot();
mem.WriteU16(intrEnv, 1);
Dispatcher.Call(cpu, mem, handler);
mem.WriteU16(intrEnv, 0);
cpu.Restore(snap);
```

`Snapshot()`/`Restore()` capture and reinstate the general-purpose registers
plus `HI`/`LO` around the call. This is equivalent to a fresh context for the
purpose that mattered — the busy-wait's live registers come back exactly as
they were, so the handler cannot corrupt them — without the complexity of
constructing and threading a second `CpuContext`, guessing a safe `SP`, or
deciding what `GP` a synthetic context should carry. The handler still runs
with the game's real stack and globals in scope, which is also closer to what
the actual PS1 interrupt path does: a real MIPS exception handler runs on the
interrupted task's own stack, it does not switch to a separate one.

The stack-scratch-space idea (`SP = game SP - 512`, registers zeroed) was the
original design and is no longer what the code does; it's recorded here only
so this section doesn't silently drift from the implementation again.

### Failure modes handled

**Reentrancy.** The handler contains loops, whose safepoints would invoke the
handler again. `InterruptController` keeps a `_delivering` flag and `Poll`
returns immediately while inside the handler. Without this the first VBlank is a
stack overflow.

**Dropped VBlanks.** If the game goes longer than 1/60s between safepoints, the
timer raises again. Pending is a counter, not a bool, so the handler runs the
times it owes and the game's internal clock does not drift — capped (4) so a
stalling machine cannot spiral.

## Testing

No tests exist in the project today; this adds `GT2Port.Tests` (xunit).

Unit, against `InterruptController` alone:
- a raised interrupt is delivered on `Poll`, exactly once
- masked interrupts defer delivery; unmasking delivers. `EnterCriticalSection`
  masks and `ExitCriticalSection` unmasks unconditionally — there is no depth,
  so a single Exit reopens delivery no matter how many Enters preceded it
  (matches the hardware and the game's own `_patch_card`/`_patch_pad` init
  routines, which call Enter repeatedly with no matching Exit)
- `Poll` inside the handler does not reenter
- three pending VBlanks run three times; beyond the cap it saturates

Integration, running the game headless:
- `0x80011DF4` passes 4 and `gt2_sysinit_vsync_setup` returns
- boot proceeds past `gt2_sysinit`, where it currently dies

The unit tests prove the mechanism is correct. Only the integration test proves
this was the actual problem.

## Out of scope

DMA, timers/RCnt, and the full I_STAT/I_MASK model. The mechanism accepts new
sources without rework; this delivery wires only VBlank.
