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

    // Joining over the relay flips the phase to Joined in the frame the button
    // is pressed, and the relay admits the player a round trip later. What was
    // built in between is a direct link with nowhere to send, and the phase
    // alone cannot tell that from a working one.

    [Fact]
    public void A_client_link_built_before_the_relay_admitted_it_is_rebuilt()
    {
        Assert.Equal(ModeHook.SocketAction.RebuildAsClient,
            ModeHook.DecideSocketAction(SessionPhase.Joined, SessionPhase.Joined,
                linkIsOverTheRelay: false, itShouldBe: true));
    }

    [Fact]
    public void And_once_it_is_over_the_relay_it_is_kept()
    {
        Assert.Equal(ModeHook.SocketAction.Keep,
            ModeHook.DecideSocketAction(SessionPhase.Joined, SessionPhase.Joined,
                linkIsOverTheRelay: true, itShouldBe: true));
    }

    /// <summary>
    /// The other direction, which is a room left and another joined by
    /// address: a link still wrapped for a room the new host is not in carries
    /// nothing, so it is rebuilt too.
    /// </summary>
    [Fact]
    public void A_link_over_a_relay_that_no_longer_carries_the_room_is_rebuilt()
    {
        Assert.Equal(ModeHook.SocketAction.RebuildAsClient,
            ModeHook.DecideSocketAction(SessionPhase.Joined, SessionPhase.Joined,
                linkIsOverTheRelay: true, itShouldBe: false));
    }

    /// <summary>
    /// And a knocking client is still a client mid-handshake: the socket it is
    /// knocking with must survive being answered.
    /// </summary>
    [Fact]
    public void Knocking_with_a_direct_link_keeps_it()
    {
        Assert.Equal(ModeHook.SocketAction.Keep,
            ModeHook.DecideSocketAction(SessionPhase.Joined, SessionPhase.Knocking,
                linkIsOverTheRelay: false, itShouldBe: false));
    }
}
