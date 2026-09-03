using GT2Port.Rendezvous;
using Xunit;

namespace GT2Relay.Tests;

/// <summary>
/// The code a player types to reach a room nobody listed.
///
/// It is read off one screen and typed into another, sometimes aloud, so the
/// alphabet leaves out the characters that get confused for one another. A
/// code that can be mistyped into a *different valid code* sends somebody into
/// a stranger's room; one that is simply refused sends them back to the box.
/// </summary>
public class RoomCodeTests
{
    [Fact]
    public void A_code_is_six_characters_of_the_alphabet()
    {
        var random = new Random(1234);

        for (int i = 0; i < 200; i++)
        {
            string code = RoomCode.Next(random);
            Assert.Equal(6, code.Length);
            Assert.All(code, c => Assert.Contains(c, RoomCode.Alphabet));
            Assert.True(RoomCode.IsWellFormed(code));
        }
    }

    [Theory]
    [InlineData('0')]
    [InlineData('1')]
    [InlineData('I')]
    [InlineData('O')]
    public void The_confusable_characters_are_not_in_it(char c)
    {
        Assert.DoesNotContain(c, RoomCode.Alphabet);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("ABC", false)]
    [InlineData("ABCDEFG", false)]
    [InlineData("ABCDE0", false)]
    [InlineData("ABCDEF", true)]
    [InlineData("234567", true)]
    public void Well_formed_is_length_and_alphabet(string code, bool ok)
    {
        Assert.Equal(ok, RoomCode.IsWellFormed(code));
    }

    /// <summary>
    /// What a person types is not what the wire carries: spaces, a lowercase
    /// keyboard, and the dash people put in the middle of anything six
    /// characters long.
    /// </summary>
    [Theory]
    [InlineData("ab23cd", "AB23CD")]
    [InlineData("  AB23CD  ", "AB23CD")]
    [InlineData("AB2-3CD", "AB23CD")]
    [InlineData("ab2 3cd", "AB23CD")]
    public void Tidy_makes_what_was_typed_into_what_was_meant(string typed, string want)
    {
        Assert.Equal(want, RoomCode.Tidy(typed));
    }

    [Fact]
    public void Tidy_leaves_something_that_cannot_be_a_code_alone_enough_to_be_refused()
    {
        Assert.False(RoomCode.IsWellFormed(RoomCode.Tidy("hello there")));
    }
}
