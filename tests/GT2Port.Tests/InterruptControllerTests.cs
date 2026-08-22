using RecompOne.Runtime.Interrupts;
using Xunit;

namespace GT2Port.Tests;

[Collection("interrupts")]
public class InterruptControllerTests
{
    public InterruptControllerTests() => InterruptController.Reset();

    [Fact]
    public void Raised_interrupt_is_delivered_once()
    {
        InterruptController.Raise();

        Assert.Equal(1, InterruptController.Drain());
        Assert.Equal(0, InterruptController.Drain());
    }

    [Fact]
    public void Critical_section_defers_delivery_until_it_closes()
    {
        InterruptController.EnterCritical();
        InterruptController.Raise();

        Assert.Equal(0, InterruptController.Drain());

        InterruptController.ExitCritical();
        Assert.Equal(1, InterruptController.Drain());
    }

    [Fact]
    public void A_single_exit_reopens_delivery_regardless_of_enter_count()
    {
        // Not a nesting primitive: any Exit unmasks, no matter how many Enters
        // preceded it. Covered more thoroughly by
        // Unmatched_nested_enters_still_deliver_after_one_exit below.
        InterruptController.EnterCritical();
        InterruptController.EnterCritical();
        InterruptController.Raise();
        InterruptController.ExitCritical();

        Assert.Equal(1, InterruptController.Drain());
    }

    [Fact]
    public void Unbalanced_exit_is_a_harmless_no_op()
    {
        InterruptController.ExitCritical();
        Assert.False(InterruptController.Masked);

        InterruptController.EnterCritical();
        InterruptController.Raise();
        Assert.Equal(0, InterruptController.Drain());
    }

    [Fact]
    public void Unmatched_nested_enters_still_deliver_after_one_exit()
    {
        // Reproduces the _patch_card/_patch_card2 shape: an outer EnterCritical,
        // two inner EnterCritical calls that never get a matching ExitCritical,
        // then a single ExitCritical from the outer caller. On real hardware
        // (and under the correct mask model) that one Exit reopens delivery.
        InterruptController.EnterCritical();
        InterruptController.EnterCritical();
        InterruptController.EnterCritical();
        InterruptController.Raise();

        InterruptController.ExitCritical();

        Assert.Equal(1, InterruptController.Drain());
    }

    [Fact]
    public void Drain_does_not_reenter_while_delivering()
    {
        InterruptController.Raise();

        using (InterruptController.BeginDelivery())
        {
            InterruptController.Raise();
            Assert.Equal(0, InterruptController.Drain());
        }

        // Both survive: the one raised before delivery and the one raised during it.
        Assert.Equal(2, InterruptController.Drain());
    }

    [Fact]
    public void Three_pending_interrupts_are_owed_three_deliveries()
    {
        InterruptController.Raise();
        InterruptController.Raise();
        InterruptController.Raise();

        Assert.Equal(3, InterruptController.Drain());
    }

    [Fact]
    public void Pending_saturates_at_the_cap()
    {
        for (int i = 0; i < 20; i++) InterruptController.Raise();

        Assert.Equal(InterruptController.MaxPending, InterruptController.Drain());
    }
}
