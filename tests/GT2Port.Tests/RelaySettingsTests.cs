using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// Where the relay lives, as a person types it.
///
/// A player is given an address by whoever runs the server, and pastes it into
/// a box. Most of them will paste it without a port, because a port is not a
/// thing most people think about - so the default has to be there.
/// </summary>
public class RelaySettingsTests
{
    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9", RelaySettings.DefaultPort)]
    [InlineData("203.0.113.9:34720", "203.0.113.9", 34720)]
    [InlineData("203.0.113.9:40000", "203.0.113.9", 40000)]
    [InlineData("  203.0.113.9  ", "203.0.113.9", RelaySettings.DefaultPort)]
    public void An_address_with_or_without_a_port_is_read(string typed, string address, int port)
    {
        Assert.True(RelaySettings.TryReadAddress(typed, out var where));

        Assert.Equal(address, where.Address.ToString());
        Assert.Equal(port, where.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an address")]
    [InlineData("203.0.113.9:0")]
    [InlineData("203.0.113.9:70000")]
    [InlineData("203.0.113.9:abc")]
    public void And_something_that_is_not_one_is_refused(string typed)
    {
        Assert.False(RelaySettings.TryReadAddress(typed, out _));
    }

    /// <summary>
    /// A name rather than an address, because that is what somebody running a
    /// server will hand out once they have a domain.
    /// </summary>
    [Fact]
    public void A_name_that_resolves_is_read()
    {
        Assert.True(RelaySettings.TryReadAddress("localhost:34720", out var where));

        Assert.Equal(34720, where.Port);
    }

    /// <summary>
    /// The shipped default is a file meant to be edited by hand, so it has to
    /// survive somebody explaining themselves in it.
    /// </summary>
    [Theory]
    [InlineData(new[] { "relay.example:9120" }, "relay.example:9120")]
    [InlineData(new[] { "# a comment", "relay.example:9120" }, "relay.example:9120")]
    [InlineData(new[] { "", "   ", "# why", "  relay.example  " }, "relay.example")]
    [InlineData(new[] { "first.example", "second.example" }, "first.example")]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "# only comments" }, "")]
    public void The_shipped_default_is_the_first_line_that_says_something(
        string[] lines, string want)
    {
        Assert.Equal(want, RelaySettings.FirstUsefulLine(lines));
    }

    /// <summary>
    /// And the file the port actually ships has to be one of those, or every
    /// player starts with a box that quietly says nothing.
    /// </summary>
    [Fact]
    public void The_file_that_ships_names_a_relay_that_can_be_reached()
    {
        string path = GameFiles.Find("config", "relay.txt");
        Assert.True(File.Exists(path), $"{path} should ship with the port");

        string shipped = RelaySettings.FirstUsefulLine(File.ReadAllLines(path));

        Assert.NotEqual("", shipped);
        Assert.True(RelaySettings.TryReadAddress(shipped, out var where),
            $"\"{shipped}\" is not an address the game could reach");
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork,
            where.Address.AddressFamily);
    }

    [Fact]
    public void No_address_configured_means_the_relay_is_simply_off()
    {
        string was = RelaySettings.Address;
        try
        {
            RelaySettings.Address = "";
            Assert.False(RelaySettings.Configured);

            RelaySettings.Address = "203.0.113.9";
            Assert.True(RelaySettings.Configured);
        }
        finally
        {
            RelaySettings.Address = was;
        }
    }
}
