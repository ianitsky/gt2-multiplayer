using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

// Task 10 review, Important 2: ModeHook.RunLobby needs a live ImGui context
// (it pumps the host window itself) so it cannot be unit-tested directly.
// The socket lifecycle decision it drives - which of keep / rebuild-as-host
// / rebuild-as-client / drop applies as the phase changes - is a pure
// function of (currentRole, phase) and is not ImGui-shaped at all, so it is
// pulled out as ModeHook.DecideSocketAction and exercised here for every
// transition RunLobby can actually encounter.
public class ModeHookTests
{
    [Fact]
    public void Browsing_to_hosting_rebuilds_as_host()
    {
        Assert.Equal(ModeHook.SocketAction.RebuildAsHost,
            ModeHook.DecideSocketAction(null, SessionPhase.Hosting));
    }

    [Fact]
    public void Browsing_to_joined_rebuilds_as_client()
    {
        Assert.Equal(ModeHook.SocketAction.RebuildAsClient,
            ModeHook.DecideSocketAction(null, SessionPhase.Joined));
    }

    [Fact]
    public void Joined_to_disconnected_drops_the_socket()
    {
        Assert.Equal(ModeHook.SocketAction.Drop,
            ModeHook.DecideSocketAction(SessionPhase.Joined, SessionPhase.Disconnected));
    }

    [Fact]
    public void Hosting_to_browsing_drops_the_socket()
    {
        Assert.Equal(ModeHook.SocketAction.Drop,
            ModeHook.DecideSocketAction(SessionPhase.Hosting, SessionPhase.Browsing));
    }

    [Fact]
    public void Hosting_to_joined_rebuilds_as_client_on_the_role_switch()
    {
        Assert.Equal(ModeHook.SocketAction.RebuildAsClient,
            ModeHook.DecideSocketAction(SessionPhase.Hosting, SessionPhase.Joined));
    }

    // Staying in the same phase two ticks running must keep the existing
    // socket rather than rebuild it - a rebuild every frame would exhaust
    // ephemeral ports.

    [Fact]
    public void Staying_hosting_keeps_the_socket()
    {
        Assert.Equal(ModeHook.SocketAction.Keep,
            ModeHook.DecideSocketAction(SessionPhase.Hosting, SessionPhase.Hosting));
    }

    [Fact]
    public void Staying_joined_keeps_the_socket()
    {
        Assert.Equal(ModeHook.SocketAction.Keep,
            ModeHook.DecideSocketAction(SessionPhase.Joined, SessionPhase.Joined));
    }
}
