using RecompOne.Runtime.Context;
using RecompOne.Runtime.Interrupts;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Sdk;
using Xunit;

namespace GT2Port.Tests;

[Collection("interrupts")]
public class LibApiTests
{
    public LibApiTests() => InterruptController.Reset();

    static (CpuContext, IMemory) Fixture() => (new CpuContext(), new PSMemory());

    [Fact]
    public void Critical_section_calls_move_the_mask_state()
    {
        var (c, m) = Fixture();

        LibApi.EnterCriticalSection(c, m);
        Assert.True(InterruptController.Masked);

        LibApi.ExitCriticalSection(c, m);
        Assert.False(InterruptController.Masked);
    }

    [Fact]
    public void A_single_exit_unmasks_regardless_of_how_many_enters_preceded_it()
    {
        var (c, m) = Fixture();

        LibApi.EnterCriticalSection(c, m);
        LibApi.EnterCriticalSection(c, m);
        LibApi.ExitCriticalSection(c, m);

        Assert.False(InterruptController.Masked);
    }

    [Fact]
    public void EnterCriticalSection_returns_one_in_v0()
    {
        var (c, m) = Fixture();

        LibApi.EnterCriticalSection(c, m);

        Assert.Equal(1u, c.V0);
    }

    [Fact]
    public void Nested_EnterCriticalSection_returns_zero_in_v0()
    {
        var (c, m) = Fixture();

        LibApi.EnterCriticalSection(c, m);
        LibApi.EnterCriticalSection(c, m);

        Assert.Equal(0u, c.V0);
    }

    [Fact]
    public void Unmatched_nested_EnterCriticalSection_calls_still_unmask_on_one_exit()
    {
        // The _patch_card/_patch_card2 shape: EnterCriticalSection called
        // repeatedly with no matching ExitCriticalSection in between. A single
        // Exit from the caller must still fully unmask.
        var (c, m) = Fixture();

        LibApi.EnterCriticalSection(c, m);
        LibApi.EnterCriticalSection(c, m);
        LibApi.ExitCriticalSection(c, m);

        Assert.False(InterruptController.Masked);
    }

    [Fact]
    public void EnterCriticalSection_returns_one_again_after_matched_pair()
    {
        var (c, m) = Fixture();

        LibApi.EnterCriticalSection(c, m);
        LibApi.ExitCriticalSection(c, m);
        LibApi.EnterCriticalSection(c, m);

        Assert.Equal(1u, c.V0);
    }

    [Fact]
    public void ExitCriticalSection_sets_v0_to_zero()
    {
        var (c, m) = Fixture();

        LibApi.EnterCriticalSection(c, m);
        LibApi.ExitCriticalSection(c, m);

        Assert.Equal(0u, c.V0);
    }
}
