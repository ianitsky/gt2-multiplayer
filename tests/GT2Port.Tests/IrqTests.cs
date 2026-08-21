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
}
