using System.Collections.Generic;
using System.Linq;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Interrupts;
using RecompOne.Runtime.Memory;
using Xunit;

namespace GT2Port.Tests;

[Collection("interrupts")]
public class IrqTests
{
    public IrqTests()
    {
        InterruptController.Reset();
        Irq.Reset();
    }

    static (CpuContext, IMemory) Fixture()
    {
        var c = new CpuContext { SP = 0x801FFF00u, GP = 0x12345678u };
        return (c, new PSMemory());
    }

    [Fact]
    public void Poll_does_nothing_when_no_interrupt_is_pending()
    {
        var (c, m) = Fixture();
        int calls = 0;
        Irq.Deliver = _ => calls++;

        Irq.Poll(c, m);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Poll_delivers_irq_zero_for_vblank()
    {
        var (c, m) = Fixture();
        var delivered = new List<int>();
        Irq.Deliver = irq => delivered.Add(irq);
        InterruptController.Raise();

        Irq.Poll(c, m);

        Assert.Equal(new[] { 0 }, delivered);
    }

    [Fact]
    public void Poll_delivers_once_per_pending_interrupt()
    {
        var (c, m) = Fixture();
        int calls = 0;
        Irq.Deliver = _ => calls++;
        InterruptController.Raise();
        InterruptController.Raise();
        InterruptController.Raise();

        Irq.Poll(c, m);

        Assert.Equal(3, calls);
    }

    [Fact]
    public void Poll_is_inert_inside_a_critical_section()
    {
        var (c, m) = Fixture();
        int calls = 0;
        Irq.Deliver = _ => calls++;
        InterruptController.EnterCritical();
        InterruptController.Raise();

        Irq.Poll(c, m);
        Assert.Equal(0, calls);

        InterruptController.ExitCritical();
        Irq.Poll(c, m);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_handler_that_polls_again_does_not_reenter()
    {
        var (c, m) = Fixture();
        int calls = 0;
        Irq.Deliver = _ =>
        {
            calls++;
            InterruptController.Raise();
            Irq.Poll(c, m);
        };
        InterruptController.Raise();

        Irq.Poll(c, m);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Poll_pumps_the_host_before_delivering()
    {
        var (c, m) = Fixture();
        var order = new List<string>();
        Irq.PumpHost = () => order.Add("pump");
        Irq.Deliver = _ => order.Add("deliver");
        InterruptController.Raise();

        Irq.Poll(c, m);

        Assert.Equal(new[] { "pump", "deliver" }, order);
    }

    [Fact]
    public void Poll_pumps_the_host_even_with_no_delivery_hook_wired()
    {
        var (c, m) = Fixture();
        int pumps = 0;
        Irq.PumpHost = () => pumps++;
        InterruptController.Raise();

        Irq.Poll(c, m);

        Assert.Equal(1, pumps);
    }

    [Fact]
    public void Wall_clock_source_raises_over_time()
    {
        int raised = 0;
        var source = new WallClockVBlankSource(hz: 1000.0);
        source.Start(() => Interlocked.Increment(ref raised));

        Thread.Sleep(200);
        source.Stop();

        Assert.True(raised > 0, $"expected the source to raise at least once, saw {raised}");
    }

    [Fact]
    public void Wall_clock_source_stops_raising_after_stop()
    {
        int raised = 0;
        var source = new WallClockVBlankSource(hz: 1000.0);
        source.Start(() => Interlocked.Increment(ref raised));
        Thread.Sleep(100);
        source.Stop();

        int afterStop = Volatile.Read(ref raised);
        Thread.Sleep(100);

        Assert.Equal(afterStop, Volatile.Read(ref raised));
    }

    [Fact]
    public void Wall_clock_source_stop_returns_promptly_at_a_low_rate()
    {
        int raised = 0;
        var source = new WallClockVBlankSource(hz: 2.0); // 500ms period
        source.Start(() => Interlocked.Increment(ref raised));
        Thread.Sleep(50); // well inside the first period; nothing has raised yet

        var sw = System.Diagnostics.Stopwatch.StartNew();
        source.Stop();
        sw.Stop();

        // The old implementation joined with a fixed 250ms timeout unrelated to the
        // 500ms period, so it would time out here. A correct Stop() wakes the worker
        // immediately via cancellation, so this should return in well under 250ms.
        Assert.True(sw.ElapsedMilliseconds < 250,
            $"Stop() took {sw.ElapsedMilliseconds}ms, expected well under 250ms");

        int afterStop = Volatile.Read(ref raised);
        Thread.Sleep(600); // longer than the 500ms period
        Assert.Equal(afterStop, Volatile.Read(ref raised));
    }

    [Fact]
    public void Wall_clock_source_start_called_twice_does_not_block_or_leave_two_threads_raising()
    {
        int raised = 0;
        var source = new WallClockVBlankSource(hz: 2.0); // 500ms period

        source.Start(() => Interlocked.Increment(ref raised));
        Thread.Sleep(20); // the first thread is still sleeping through most of its period

        var sw = System.Diagnostics.Stopwatch.StartNew();
        source.Start(() => Interlocked.Increment(ref raised)); // internally stops the first thread
        sw.Stop();

        // Start() begins with Stop(). The old Stop() joined the still-sleeping first
        // thread with a fixed 250ms timeout unrelated to the 500ms period, so restarting
        // here would block for ~250ms. A correct Stop() wakes it immediately.
        Assert.True(sw.ElapsedMilliseconds < 250,
            $"Start() took {sw.ElapsedMilliseconds}ms to restart, expected well under 250ms");

        source.Stop();

        int afterStop = Volatile.Read(ref raised);
        // Longer than the period: if a stray first-generation thread survived (the old
        // code's bug), or two threads ended up raising concurrently, this would catch it.
        Thread.Sleep(600);
        Assert.Equal(afterStop, Volatile.Read(ref raised));
    }

    [Fact]
    public void Wall_clock_source_stop_without_start_does_not_throw()
    {
        var source = new WallClockVBlankSource(hz: 2.0);

        var ex = Record.Exception(() => source.Stop());

        Assert.Null(ex);
    }

    [Fact]
    public void Wall_clock_source_stop_called_twice_does_not_throw()
    {
        int raised = 0;
        var source = new WallClockVBlankSource(hz: 2.0);
        source.Start(() => Interlocked.Increment(ref raised));

        source.Stop();
        var ex = Record.Exception(() => source.Stop());

        Assert.Null(ex);
    }

    [Fact]
    public void Poll_delivers_every_pending_channel()
    {
        var (c, m) = Fixture();
        var delivered = new List<int>();
        Irq.Deliver = irq => delivered.Add(irq);
        InterruptController.Raise(0);
        InterruptController.Raise(2);

        Irq.Poll(c, m);

        Assert.Equal(new[] { 0, 2 }, delivered.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Poll_delivers_a_channel_once_per_raise()
    {
        var (c, m) = Fixture();
        int cdDeliveries = 0;
        Irq.Deliver = irq => { if (irq == 2) cdDeliveries++; };
        InterruptController.Raise(2);
        InterruptController.Raise(2);
        InterruptController.Raise(2);

        Irq.Poll(c, m);

        Assert.Equal(3, cdDeliveries);
    }

    [Fact]
    public void Poll_with_only_a_cd_interrupt_does_not_deliver_vblank()
    {
        var (c, m) = Fixture();
        var delivered = new List<int>();
        Irq.Deliver = irq => delivered.Add(irq);
        InterruptController.Raise(2);

        Irq.Poll(c, m);

        Assert.Equal(new[] { 2 }, delivered.ToArray());
    }
}
