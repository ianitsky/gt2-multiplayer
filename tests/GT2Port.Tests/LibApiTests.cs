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
    public void Critical_section_calls_move_the_depth()
    {
        var (c, m) = Fixture();

        LibApi.EnterCriticalSection(c, m);
        Assert.Equal(1, InterruptController.CriticalDepth);

        LibApi.ExitCriticalSection(c, m);
        Assert.Equal(0, InterruptController.CriticalDepth);
    }

    [Fact]
    public void Nested_critical_sections_nest()
    {
        var (c, m) = Fixture();

        LibApi.EnterCriticalSection(c, m);
        LibApi.EnterCriticalSection(c, m);
        LibApi.ExitCriticalSection(c, m);

        Assert.Equal(1, InterruptController.CriticalDepth);
    }

    [Fact]
    public void EnterCriticalSection_returns_one_in_v0()
    {
        var (c, m) = Fixture();

        LibApi.EnterCriticalSection(c, m);

        Assert.Equal(1u, c.V0);
    }
}
