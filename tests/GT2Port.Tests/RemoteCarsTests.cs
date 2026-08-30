using GT2Port.Multiplayer;
using RecompOne.Runtime.Memory;
using Xunit;

namespace GT2Port.Tests;

public class RemoteCarsTests
{
    [Fact]
    public void ReadsBackWhatItWrote()
    {
        var m = new PSMemory();
        var place = new RemoteCars.Place(603221, 1529339, 1030);

        RemoteCars.Write(m, 3, place);

        Assert.Equal(place, RemoteCars.Read(m, 3));
    }

    /// <summary>
    /// The game keeps a car's place twice, 0x24 apart, and both copies moved
    /// together in every sample of a six-car race. Writing one and not the
    /// other would leave whatever reads the second disagreeing about where the
    /// car is.
    /// </summary>
    [Fact]
    public void WritesBothCopiesTheGameKeeps()
    {
        var m = new PSMemory();
        uint car = RemoteCars.FirstCar + 2u * RemoteCars.CarStride;

        RemoteCars.Write(m, 2, new RemoteCars.Place(11, 22, 33));

        Assert.Equal(11u, m.ReadU32(car + RemoteCars.X + RemoteCars.SecondCopy));
        Assert.Equal(22u, m.ReadU32(car + RemoteCars.Z + RemoteCars.SecondCopy));
        Assert.Equal(33u, m.ReadU32(car + RemoteCars.Y + RemoteCars.SecondCopy));
    }

    /// <summary>Each car is its own 0xB40, so writing one must not move another.</summary>
    [Fact]
    public void LeavesTheOtherCarsAlone()
    {
        var m = new PSMemory();
        RemoteCars.Write(m, 0, new RemoteCars.Place(1, 2, 3));

        RemoteCars.Write(m, 1, new RemoteCars.Place(99, 98, 97));

        Assert.Equal(new RemoteCars.Place(1, 2, 3), RemoteCars.Read(m, 0));
    }

    /// <summary>
    /// A transform is nine words - the place and then a 3x3 with its rows
    /// padded to eight bytes - and the game keeps the whole of it twice.
    /// Copying only the place gave a ghost that drove where the player drove
    /// while still facing wherever it had started.
    /// </summary>
    [Fact]
    public void CarriesTheRotationAsWellAsThePlace()
    {
        var m = new PSMemory();
        var transform = new uint[RemoteCars.TransformWords];
        for (int i = 0; i < transform.Length; i++) transform[i] = (uint)(0x1000 + i);

        RemoteCars.WriteTransform(m, 4, transform);

        Assert.Equal(transform, RemoteCars.ReadTransform(m, 4));
    }

    [Fact]
    public void WritesTheWholeTransformIntoBothCopies()
    {
        var m = new PSMemory();
        uint at = RemoteCars.FirstCar + 1u * RemoteCars.CarStride + RemoteCars.Transform;
        var transform = new uint[RemoteCars.TransformWords];
        for (int i = 0; i < transform.Length; i++) transform[i] = (uint)(500 + i);

        RemoteCars.WriteTransform(m, 1, transform);

        for (uint i = 0; i < RemoteCars.TransformWords; i++)
            Assert.Equal(500u + i, m.ReadU32(at + RemoteCars.SecondCopy + i * 4u));
    }

    /// <summary>
    /// The game stores a rotation as a 3x3 of sixteen-bit values in rows of
    /// four shorts, so the diagonal falls on shorts 0, 5 and 10 - which is how
    /// a car going straight reads 4095, -4092, 4093 there. A yaw of nothing
    /// has to come out the same shape, or the ghost is being handed nonsense.
    /// </summary>
    [Fact]
    public void BuildsAYawTheWayTheGameStoresOne()
    {
        var words = RemoteCars.YawFor(0);

        short[] m = new short[12];
        for (int i = 0; i < words.Length; i++)
        {
            m[i * 2] = (short)(words[i] & 0xFFFF);
            m[i * 2 + 1] = (short)(words[i] >> 16);
        }

        Assert.Equal(4096, m[0]);    // the diagonal, at 0, 5 and 10
        Assert.Equal(-4096, m[5]);   // negative, because Y counts downwards
        Assert.Equal(4096, m[10]);
        Assert.Equal(0, m[1]);
        Assert.Equal(0, m[2]);
    }

    /// <summary>A quarter turn swaps the two horizontal terms, which is what makes it visible.</summary>
    [Fact]
    public void TurnsAQuarterCircleIntoTheOffDiagonal()
    {
        var words = RemoteCars.YawFor(90);

        short m00 = (short)(words[0] & 0xFFFF);
        short m02 = (short)(words[1] & 0xFFFF);

        Assert.Equal(0, m00);
        Assert.Equal(4096, m02);
    }
}
