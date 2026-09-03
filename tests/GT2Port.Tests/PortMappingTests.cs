using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// The part that talks to a router cannot be tested without one. What can be,
/// and what has actually gone wrong in things like this, is the lifecycle
/// around it: something called every frame that starts a second attempt each
/// time is a machine sending a hundred SOAP requests a second at its own
/// router.
/// </summary>
public class PortMappingTests
{
    [Fact]
    public void It_starts_off()
    {
        PortMapping.Release();

        Assert.Equal(PortMapping.How.Off, PortMapping.State);
        Assert.Null(PortMapping.Outside);
    }

    /// <summary>
    /// Want is called from the lobby, every frame, for as long as a room is
    /// open. The second call and the ten-thousandth must do nothing.
    /// </summary>
    [Fact]
    public void Wanting_it_over_and_over_only_tries_once()
    {
        PortMapping.Release();

        for (int i = 0; i < 500; i++) PortMapping.Want(34719);

        Assert.Equal(1, PortMapping.Attempts);
        Assert.NotEqual(PortMapping.How.Off, PortMapping.State);

        PortMapping.Release();
    }

    [Fact]
    public void Releasing_something_that_was_never_mapped_is_nothing()
    {
        PortMapping.Release();
        PortMapping.Release();

        Assert.Equal(PortMapping.How.Off, PortMapping.State);
    }

    /// <summary>
    /// And after letting go, wanting it again is a fresh attempt - a player
    /// who closes a room and opens another must not be stuck with whatever
    /// the first one concluded.
    /// </summary>
    [Fact]
    public void And_after_letting_go_it_will_try_again()
    {
        PortMapping.Release();
        PortMapping.Want(34719);
        PortMapping.Release();
        PortMapping.Want(34719);

        Assert.Equal(1, PortMapping.Attempts);

        PortMapping.Release();
    }

    [Fact]
    public void Switched_off_it_never_tries()
    {
        PortMapping.Release();
        Environment.SetEnvironmentVariable("GT2_UPNP", "0");
        try
        {
            PortMapping.Wanted = PortMapping.ReadWanted();
            PortMapping.Want(34719);

            Assert.Equal(PortMapping.How.Off, PortMapping.State);
            Assert.Equal(0, PortMapping.Attempts);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GT2_UPNP", null);
            PortMapping.Wanted = PortMapping.ReadWanted();
            PortMapping.Release();
        }
    }
}
