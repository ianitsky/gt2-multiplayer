using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// Every case here is a pair the game printed itself: the roster it builds at
/// 0x801E18E0 listed these numbers, and CourseTable already carried these
/// codes. If the rotate ever drifts, these stop matching.
/// </summary>
public class CourseIdTests
{
    [Theory]
    [InlineData("tahiti_t", 0x6AD87E5Eu)]
    [InlineData("tahiti_t_2p", 0xF97FA851u)]
    [InlineData("tahiti_t_rev", 0x5FEE1234u)]
    [InlineData("tahiti_d_new", 0x4FEDD235u)]
    [InlineData("sprint2", 0x73AB047Eu)]
    [InlineData("2p_sprint2", 0x809C807Eu)]
    [InlineData("rev_sprint2", 0xAD628085u)]
    [InlineData("s_speed", 0x34C670ECu)]
    [InlineData("speed2p", 0x669A543Cu)]
    [InlineData("parma", 0x718B3BA1u)]
    [InlineData("parma_2p", 0xEE8BC31Cu)]
    [InlineData("testline", 0x35B88252u)]
    public void MatchesTheNumberTheGameListedForThatCode(string code, uint id) =>
        Assert.Equal(id, CourseId.Of(code));

    /// <summary>
    /// The three Tahiti Roads are why the code is what gets hashed. Matching a
    /// room's course by the name a player reads would pick whichever came
    /// first, which is a different track two times in three.
    /// </summary>
    [Fact]
    public void TellsTheThreeCoursesCalledTahitiRoadApart()
    {
        Assert.NotEqual(CourseId.Of("tahiti_t"), CourseId.Of("tahiti_t_2p"));
        Assert.NotEqual(CourseId.Of("tahiti_t_2p"), CourseId.Of("tahiti_t_rev"));
        Assert.NotEqual(CourseId.Of("tahiti_t"), CourseId.Of("tahiti_t_rev"));
    }

    /// <summary>Every course the two-player roster offers hashes to its own number.</summary>
    [Fact]
    public void GivesEveryTwoPlayerCourseADistinctNumber()
    {
        var ids = CourseTable.All.Select(c => CourseId.Of(c.Code)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
