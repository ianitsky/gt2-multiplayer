using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class CourseTableTests
{
    [Fact]
    public void Holds_the_whole_two_player_roster()
    {
        Assert.Equal(27, CourseTable.All.Count);
        Assert.Equal(21, CourseTable.All.Count(c => c.Surface == CourseSurface.Tarmac));
        Assert.Equal(6, CourseTable.All.Count(c => c.Surface == CourseSurface.Dirt));
    }

    [Fact]
    public void Every_course_has_a_unique_code_and_a_name()
    {
        Assert.Equal(CourseTable.All.Count, CourseTable.All.Select(c => c.Code).Distinct().Count());
        Assert.All(CourseTable.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Code)));
        Assert.All(CourseTable.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
    }

    [Theory]
    [InlineData("2p_mountain", "Trial Mountain Circuit")]
    [InlineData("speed2p", "Super Speedway")]
    [InlineData("Gtest", "Grindelwald")]
    public void Pairs_the_codes_the_game_pairs(string code, string name)
    {
        Assert.Equal(name, CourseTable.DisplayName(code));
    }

    [Fact]
    public void An_unknown_code_shows_as_itself()
    {
        Assert.False(CourseTable.TryFind("not_a_course", out _));
        Assert.Equal("not_a_course", CourseTable.DisplayName("not_a_course"));
    }
}
