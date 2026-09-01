using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// What the port does when nobody has set anything.
///
/// These exist because of a real failure and not for tidiness. Launching a
/// room's race and moving the other players' cars were both off unless an
/// environment variable said otherwise - right while each was new and unproven,
/// and a trap once they were the only way anybody played. Run from a plain
/// .exe, the host pressed Start, every machine agreed a race, and the game
/// walked into Simulation instead of running it.
///
/// A default that only holds on the machine the developer tests on is not a
/// default. These fail on any build that quietly goes back to needing a shell
/// full of variables.
/// </summary>
public class DefaultsTests
{
    [Fact]
    public void A_rooms_race_is_launched_rather_than_walked_to_through_the_menus()
    {
        Assert.True(DirectRace.Enabled);
    }

    [Fact]
    public void And_the_other_players_cars_are_moved()
    {
        Assert.True(CarSync.Enabled);
    }
}
