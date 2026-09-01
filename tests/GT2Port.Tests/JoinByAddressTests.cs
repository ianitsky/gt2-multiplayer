using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// Reaching a host that never announced itself, which is every host that is not
/// on this network. A port open to the internet is scanned within hours, so the
/// room carries a secret and the host checks it.
/// </summary>
public class JoinByAddressTests
{
    [Fact]
    public void A_rooms_secret_never_goes_out_in_its_room_state()
    {
        var session = new Session("ian", () => DateTime.UtcNow);
        session.Host("ian's room", "seattle_short", "special", 6, secret: "hunter2");

        Assert.Equal("hunter2", session.Current!.Secret);

        Assert.True(RoomState.TryDeserialise(
            RoomState.Serialise(session.Current!), out var published));

        Assert.Equal("", published.Secret);
    }

    /// <summary>
    /// The secret is checked before anything else is believed. A room with no
    /// secret lets anybody in, which is what a room on a local network has
    /// always done.
    /// </summary>
    [Theory]
    [InlineData("", "", true)]
    [InlineData("", "anything", true)]
    [InlineData("hunter2", "hunter2", true)]
    [InlineData("hunter2", "", false)]
    [InlineData("hunter2", "wrong", false)]
    public void A_client_is_let_in_only_when_it_says_the_rooms_secret(
        string roomSecret, string said, bool letIn)
    {
        Assert.Equal(letIn, LanSession.SaidTheSecret(roomSecret, said));
    }
}
