using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// What the three angles at +0x1F4 mean, checked against a car that was really
/// driving.
///
/// The game does not keep a car's rotation. It keeps three angles and rebuilds
/// the rotation from them every frame, in
/// gt2_ovr1_race_rotation_matrix_from_three_angles, as
///
///     row0 = ( cz*sy*sx - sz*cx ,  sz*sy*sx + cz*cx ,  cy*sx )
///     row1 = (      cz*cy       ,       sz*cy       ,   -sy  )
///     row2 = ( cz*sy*cx + sz*sx ,  sz*sy*cx - cz*sx ,  cy*cx )
///
/// with sine and cosine read from one table in which cosine is the same table
/// a quarter turn along, and every product taken in twelve-bit fixed point.
///
/// That formula is the whole of the multiplayer heading, so it is worth more
/// than a reading of the disassembly. Car zero, driving down +X, read the
/// matrix below at +0x218. Exactly one angle triple in a wide search puts that
/// matrix back, to within two parts in four thousand - which is what says the
/// three shorts are the right three shorts, in the right order, signed.
/// </summary>
public class CarSyncHeadingTests
{
    /// <summary>What car zero's rotation read while it drove down +X.</summary>
    static readonly int[,] Measured =
    {
        { 4095,  -13,    37 },
        {  -11, -4092,  -151 },
        {  -37,  -149,  4093 },
    };

    /// <summary>The angles that put it back: flat on the road, a quarter turn round.</summary>
    const short AroundX = 6;
    const short AroundY = 24;
    const short AroundZ = -1026;

    /// <summary>How far off a rebuilt term may be, in the game's own units.</summary>
    const int Close = 2;

    static short Sine(int a) =>
        (short)Math.Round(RemoteCars.WholeTurn
                          * Math.Sin(2 * Math.PI * a / RemoteCars.WholeTurn));

    static short Cosine(int a) =>
        (short)Math.Round(RemoteCars.WholeTurn
                          * Math.Cos(2 * Math.PI * a / RemoteCars.WholeTurn));

    /// <summary>A product in twelve-bit fixed point, truncated towards zero as the game does.</summary>
    static int Times(int a, int b) => a * b / RemoteCars.WholeTurn;

    /// <summary>The rebuild, written the way the game writes it.</summary>
    static int[,] RotationFrom(int ax, int ay, int az)
    {
        int sx = Sine(ax), cx = Cosine(ax);
        int sy = Sine(ay), cy = Cosine(ay);
        int sz = Sine(az), cz = Cosine(az);
        int czsy = Times(cz, sy), szsy = Times(sz, sy);

        return new[,]
        {
            { Times(czsy, sx) - Times(sz, cx), Times(szsy, sx) + Times(cz, cx), Times(cy, sx) },
            { Times(cz, cy),                   Times(sz, cy),                   -sy           },
            { Times(czsy, cx) + Times(sz, sx), Times(szsy, cx) - Times(cz, sx), Times(cy, cx) },
        };
    }

    [Fact]
    public void ThreeAnglesPutBackTheMatrixARealCarRead()
    {
        var built = RotationFrom(AroundX, AroundY, AroundZ);

        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 3; col++)
                Assert.InRange(built[row, col],
                    Measured[row, col] - Close, Measured[row, col] + Close);
    }

    /// <summary>
    /// The third angle is the heading: it alone turns a car about the axis it
    /// stands on, so turning it must move the two horizontal terms of the
    /// middle row and leave the vertical one where it was.
    /// </summary>
    [Fact]
    public void TheThirdAngleIsTheOneThatTurnsTheCar()
    {
        var straight = RotationFrom(AroundX, AroundY, 0);
        var quarter = RotationFrom(AroundX, AroundY, RemoteCars.WholeTurn / 4);

        Assert.Equal(straight[1, 2], quarter[1, 2]);
        Assert.InRange(straight[1, 0], 4090, 4096);
        Assert.InRange(quarter[1, 0], -3, 3);
        Assert.InRange(quarter[1, 1], 4090, 4096);
    }

    /// <summary>
    /// A car flat on a road has almost no rotation about the other two, which
    /// is why every one of the six cars read near the identity at once - the
    /// thing that was once taken for proof that these words were not a
    /// rotation at all.
    /// </summary>
    [Fact]
    public void TheOtherTwoAreASmallLeanOnTheRoad()
    {
        Assert.InRange(AroundX, -RemoteCars.WholeTurn / 64, RemoteCars.WholeTurn / 64);
        Assert.InRange(AroundY, -RemoteCars.WholeTurn / 64, RemoteCars.WholeTurn / 64);

        var flat = RotationFrom(0, 0, AroundZ);

        Assert.Equal(0, flat[1, 2]);
        Assert.Equal(RemoteCars.WholeTurn, flat[2, 2]);
    }
}
