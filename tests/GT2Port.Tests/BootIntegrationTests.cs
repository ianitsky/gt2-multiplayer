using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Interrupts;
using RecompOne.Runtime.Memory;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// Drives the real recompiled gt2_sysinit_vsync_setup, whose busy-wait on the
/// vblank counter at 0x80011DF4 is what hung the port. The game code, the
/// safepoint and the interrupt bookkeeping are all real; only the handler and
/// the passage of time stand in.
/// </summary>
[Collection("interrupts")]
public class BootIntegrationTests
{
    const uint VBlankCounter = 0x80011DF4u;

    public BootIntegrationTests()
    {
        InterruptController.Reset();
        Irq.Reset();
    }

    [Fact]
    public void Vsync_setup_completes_once_vblanks_are_delivered()
    {
        var m = new PSMemory();
        var c = new CpuContext { SP = 0x801FFF00u, GP = 0u };
        c.FP = c.SP;

        Dispatcher.Register("main", new Recompiled.MainDispatchTable());
        Dispatcher.Load("main");

        // gt2_sysinit_vsync_setup calls VSyncCallback before it ever reaches the
        // busy-wait this test is about. VSyncCallback reads a table pointer from
        // 0x800A8C04 (computed in the generated code as 0x800B0000 - 0x73FC) and
        // then a function pointer from table+0x14, and dispatches to it. In a
        // real boot that table is populated by earlier init code the test
        // intentionally does not run. Here we seed just enough scratch RAM for
        // that indirection to resolve to a real, verified no-op function
        // (gt2_unknown_nop0 at 0x8005F788, whose generated body is exactly
        // `return;`, registered in MainDispatchTable) so VSyncCallback completes
        // harmlessly and control reaches the actual subject of this test: the
        // vblank busy-wait at 0x80011DF4. 0x801F0000 is unused scratch RAM,
        // clear of both the vblank counter and the stack at 0x801FFF00.
        const uint VSyncCallbackTableAddr = 0x800A8C04u;
        const uint VSyncCallbackTable = 0x801F0000u;
        const uint NoOpFunction = 0x8005F788u; // gt2_unknown_nop0: body is `return;`
        m.WriteU32(VSyncCallbackTableAddr, VSyncCallbackTable);
        m.WriteU32(VSyncCallbackTable + 0x14u, NoOpFunction);

        // Stands in for gt2_vsync_handler, which does exactly this increment.
        Irq.Deliver = _ => m.WriteU32(VBlankCounter, m.ReadU32(VBlankCounter) + 1u);
        Irq.PumpHost = () => { };

        var source = new TestVBlankSource();
        source.Start(InterruptController.Raise);

        var done = Task.Run(() => Recompiled.GranTurismo2.gt2_sysinit_vsync_setup(c, m));

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)),
            "gt2_sysinit_vsync_setup did not return; the busy-wait never saw the counter advance");
        Assert.True((int)m.ReadU32(VBlankCounter) >= 4,
            $"vblank counter reached {(int)m.ReadU32(VBlankCounter)}, expected at least 4");

        source.Stop();
    }

    sealed class TestVBlankSource : IVBlankSource
    {
        CancellationTokenSource? _cts;

        public void Start(Action raise)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            new Thread(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    raise();
                    Thread.Sleep(1);
                }
            }) { IsBackground = true }.Start();
        }

        public void Stop() => _cts?.Cancel();
    }
}
