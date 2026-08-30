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
    /// A pose is where a car is and the three angles it faces along, and both
    /// have to survive the round trip or a remote car is drawn wrong.
    /// </summary>
    [Fact]
    public void ReadsBackTheWholePoseItWrote()
    {
        var m = new PSMemory();
        var pose = new RemoteCars.Pose(new RemoteCars.Place(-9, 8, -7), 37, 151, -1024);

        RemoteCars.WritePose(m, 5, pose);

        Assert.Equal(pose, RemoteCars.ReadPose(m, 5));
    }

    /// <summary>
    /// The angles are three shorts at +0x1F4, thirty bytes ahead of the place.
    /// They are read there by the game's own rebuild, so an offset off by two
    /// would not fail loudly - it would quietly steer by the wrong axis.
    /// </summary>
    [Fact]
    public void PutsTheAnglesWhereTheGameRebuildsFrom()
    {
        var m = new PSMemory();
        uint at = RemoteCars.FirstCar + 2u * RemoteCars.CarStride + RemoteCars.Heading;

        RemoteCars.WritePose(m, 2, new RemoteCars.Pose(new RemoteCars.Place(0, 0, 0), 1, 2, 3));

        Assert.Equal(0x1F4u, RemoteCars.Heading);
        Assert.Equal(1, (short)m.ReadU16(at));
        Assert.Equal(2, (short)m.ReadU16(at + 2u));
        Assert.Equal(3, (short)m.ReadU16(at + 4u));
    }

    /// <summary>
    /// Half a turn and more reads negative, and the game masks an angle to
    /// twelve bits before using it - so a heading stored unsigned would come
    /// back as a different direction entirely.
    /// </summary>
    [Fact]
    public void KeepsAnAngleSignedBothWays()
    {
        var m = new PSMemory();
        var pose = new RemoteCars.Pose(new RemoteCars.Place(0, 0, 0), short.MinValue, -1, 2047);

        RemoteCars.WritePose(m, 0, pose);

        Assert.Equal(pose, RemoteCars.ReadPose(m, 0));
    }

    /// <summary>
    /// The rotation at +0x218 is the game's to write, not ours. Writing it as
    /// well would be writing over the answer with the question - and it is
    /// what every earlier attempt at a heading did.
    /// </summary>
    [Fact]
    public void LeavesTheRotationForTheGameToRebuild()
    {
        var m = new PSMemory();
        uint at = RemoteCars.FirstCar + RemoteCars.Transform;
        for (uint i = 0; i < RemoteCars.TransformWords; i++) m.WriteU32(at + i * 4u, 0xABCDEF00u + i);

        RemoteCars.WritePose(m, 0, new RemoteCars.Pose(new RemoteCars.Place(1, 2, 3), 4, 5, 6));

        // The place is ours to write; the six words of rotation after it are not.
        for (uint i = 3; i < RemoteCars.TransformWords; i++)
            Assert.Equal(0xABCDEF00u + i, m.ReadU32(at + i * 4u));
    }
}
