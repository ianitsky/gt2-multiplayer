using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class CarTableTests
{
    [Fact]
    public void Holds_the_five_arcade_groups()
    {
        Assert.Equal(
            new[] { "special", "a", "b", "c", "rally" },
            CarTable.Arcade.Select(g => g.Id).ToArray());
        Assert.Equal(
            new[] { 10, 8, 9, 9, 24 },
            CarTable.Arcade.Select(g => g.Cars.Count).ToArray());
    }

    [Fact]
    public void Every_group_is_named_and_every_code_is_five_characters()
    {
        Assert.All(CarTable.Arcade, g => Assert.False(string.IsNullOrWhiteSpace(g.Name)));
        Assert.All(CarTable.Arcade, g => Assert.All(g.Cars, c => Assert.Equal(5, c.Length)));
    }

    [Fact]
    public void No_car_appears_in_two_groups()
    {
        var all = CarTable.Arcade.SelectMany(g => g.Cars).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
