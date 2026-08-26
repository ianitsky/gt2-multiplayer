using GT2Port.Multiplayer;
using RecompOne.Runtime.Memory;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// The roster is read out of a layout worked out from 0x80060EB4, and a layout
/// worked out is a layout that can be wrong. These lay one out by hand and
/// check the reader agrees with the search the game does.
/// </summary>
public class CourseRosterTests
{
    const uint Table = 0x801E18E0u;
    const uint Names = 0x801E1C00u;

    static IMemory WithCourses(params (uint Id, string Name)[] courses)
    {
        var m = new PSMemory();
        m.WriteU16(Table + 0x06u, (ushort)courses.Length);

        uint name = Names;
        for (int i = 0; i < courses.Length; i++)
        {
            uint entry = Table + 0x08u + (uint)(i * 0x18);
            m.WriteU32(entry + 0x00u, name);
            m.WriteU32(entry + 0x04u, courses[i].Id);

            foreach (char c in courses[i].Name) m.WriteU8(name++, (byte)c);
            m.WriteU8(name++, 0);
        }
        return m;
    }

    [Fact]
    public void ReadsEveryCourseTheGameHasReady()
    {
        var m = WithCourses((0x6AD87E5Eu, "Tahiti Road"), (0x11223344u, "Rome Circuit"));

        var courses = CourseRoster.Read(m);

        Assert.Equal(2, courses.Count);
        Assert.Equal("Tahiti Road", courses[0].Name);
        Assert.Equal(0x6AD87E5Eu, courses[0].Id);
        Assert.Equal("Rome Circuit", courses[1].Name);
        Assert.Equal(0x11223344u, courses[1].Id);
    }

    [Fact]
    public void FindsACourseByTheNameTheGameUses()
    {
        var m = WithCourses((0x6AD87E5Eu, "Tahiti Road"), (0x11223344u, "Rome Circuit"));

        Assert.True(CourseRoster.TryFind(m, "Rome Circuit", out uint id));
        Assert.Equal(0x11223344u, id);
    }

    [Fact]
    public void SaysNoRatherThanGuessingWhenTheCourseIsNotReady()
    {
        var m = WithCourses((0x6AD87E5Eu, "Tahiti Road"));

        Assert.False(CourseRoster.TryFind(m, "Apricot Hill Speedway", out uint id));
        Assert.Equal(0u, id);
    }

    /// <summary>
    /// Before the game builds the roster the header is zeroes, and a launch that
    /// read that as "zero courses, all of them at 0x00000000" would set a race
    /// to whatever a null pointer spells. Empty is the honest answer.
    /// </summary>
    [Fact]
    public void ReadsNothingFromARosterTheGameHasNotBuiltYet()
    {
        Assert.Empty(CourseRoster.Read(new PSMemory()));
        Assert.False(CourseRoster.TryFind(new PSMemory(), "Tahiti Road", out _));
    }
}
