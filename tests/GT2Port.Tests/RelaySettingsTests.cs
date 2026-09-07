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
    /// And the example beside it has to be one of those, or the file somebody
    /// copies is a file that quietly says nothing.
    ///
    /// The example rather than relay.txt itself, because relay.txt is a
    /// per-machine choice and is not in the repository: the address in it is
    /// whoever built this copy, and a tunnel address handed out for free is
    /// theirs the way a phone number is.
    ///
    /// The line is read rather than resolved. Resolving it was a name lookup
    /// against a live server in the middle of a unit test, so the suite failed
    /// whenever that tunnel was rotated, the machine was offline, or a
    /// resolver was slow - none of which say anything about this code. What
    /// the example has to get right is its shape.
    /// </summary>
    [Fact]
    public void The_example_beside_it_shows_a_line_the_game_could_read()
    {
        string path = GameFiles.Find("config", "relay.txt.example");
        Assert.True(File.Exists(path), $"{path} should ship with the port");

        string shipped = RelaySettings.FirstUsefulLine(File.ReadAllLines(path));

        Assert.NotEqual("", shipped);

        int colon = shipped.LastIndexOf(':');
        Assert.True(colon > 0, $"\"{shipped}\" shows no port, and the example should");
        Assert.True(int.TryParse(shipped[(colon + 1)..], out int port) && port is > 0 and <= 65535,
            $"\"{shipped}\" names a port the game would refuse");
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
