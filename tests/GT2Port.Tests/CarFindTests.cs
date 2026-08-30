using GT2Port.Multiplayer;
using RecompOne.Runtime.Memory;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// The scan walks every word of two megabytes of raw RAM, so it meets
/// 0x80000000 - and Math.Abs of the most negative int throws rather than
/// returning anything. That crashed a race a few frames in.
/// </summary>
public class CarFindTests
{
    [Fact]
    public void SurvivesTheMostNegativeWordInRam()
    {
        var m = new PSMemory();
        for (uint at = 0x80000000u; at < 0x80000000u + 64u; at += 4)
            m.WriteU32(at, 0x80000000u);

        var ex = Record.Exception(() => CarFind.Scan(m));

        Assert.Null(ex);
    }

    /// <summary>
    /// And still finds a position written twice, which is the whole signature:
    /// three plausible words repeated 0x24 on.
    /// </summary>
    [Fact]
    public void FindsAPositionThatIsWrittenTwice()
    {
        var m = new PSMemory();
        const uint car = 0x800A9D10u;
        foreach (uint at in new[] { car, car + 0x24u })
        {
            m.WriteU32(at + 0u, unchecked((uint)-828871));
            m.WriteU32(at + 4u, unchecked((uint)-1401524));
            m.WriteU32(at + 8u, 506);
        }

        Assert.Contains(car, CarFind.Scan(m));
    }
}
