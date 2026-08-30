using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// A car pointing down +X is a rotation of nothing: car zero's matrix read
/// within a few units of the identity while it travelled 465787 along X and
/// 2138 along Z. The forward axis is the row that reads (cos, 0, sin), which
/// puts +Z at a quarter turn - and getting that backwards would draw every
/// remote car sideways.
/// </summary>
public class CarSyncHeadingTests
{
    static (short Cos, short Sin) Forward(double degrees)
    {
        var w = RemoteCars.YawFor(degrees);
        return ((short)(w[0] & 0xFFFF), (short)(w[1] & 0xFFFF));
    }

    [Fact]
    public void PointsDownXWhenTheRotationIsNothing()
    {
        var (cos, sin) = Forward(0);

        Assert.Equal(4096, cos);
        Assert.Equal(0, sin);
    }

    [Fact]
    public void PointsDownZAtAQuarterTurn()
    {
        var (cos, sin) = Forward(90);

        Assert.Equal(0, cos);
        Assert.Equal(4096, sin);
    }

    [Fact]
    public void TurnsAllTheWayRound()
    {
        Assert.Equal(Forward(0), Forward(360));
        Assert.Equal((short)-4096, Forward(180).Cos);
    }
}
