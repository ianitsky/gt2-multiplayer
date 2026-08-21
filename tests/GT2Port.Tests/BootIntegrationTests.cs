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
