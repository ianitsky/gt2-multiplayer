using System.Text;
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class CarInfoTests
{
    const string Alphabet = "-0123456789abcdefghijklmnopqrstuvwxyz";

    static uint Pack(string code)
    {
        uint packed = 0;
        for (int i = 0; i < 5; i++)
            packed |= (uint)Alphabet.IndexOf(code[i]) << ((4 - i) * 6);
        return packed;
    }

    /// <summary>
    /// Builds a car database the way the disc stores one: fixed records first,
    /// then the variable field blocks they point at. Each block is given a
    /// binary prefix of its own length, because on the disc that prefix varies
    /// and a reader must not assume it away.
    /// </summary>
    static byte[] BuildDatabase(params (string Code, byte[] Prefix, string Name)[] cars)
    {
        var blocks = new List<byte[]>();
        foreach (var (_, prefix, name) in cars)
        {
            var text = Encoding.ASCII.GetBytes(name);
            var block = new List<byte>(prefix) { (byte)text.Length };
            block.AddRange(text);
            block.Add(0);
            blocks.Add(block.ToArray());
        }

        int blockBase = 8 + cars.Length * 8;
        var image = new List<byte>(Encoding.ASCII.GetBytes("CAR\0"));
        image.AddRange(BitConverter.GetBytes((uint)cars.Length));

        int at = blockBase;
        for (int i = 0; i < cars.Length; i++)
        {
            image.AddRange(BitConverter.GetBytes(Pack(cars[i].Code)));
            image.AddRange(BitConverter.GetBytes((ushort)at));
            image.AddRange(BitConverter.GetBytes((ushort)0));
            at += blocks[i].Length;
        }
        foreach (var block in blocks) image.AddRange(block);
        return image.ToArray();
    }

    [Fact]
    public void Reads_a_name_through_the_packed_code()
    {
        var db = BuildDatabase(("a-a7r", [0xC5, 0x25, 0xA8], "Mazda RX-7 A-spec LM"));

        var info = CarInfo.TryParse(db);

        Assert.NotNull(info);
        Assert.Equal(1, info!.Count);
        Assert.True(info.TryName("a-a7r", out var name));
        Assert.Equal("Mazda RX-7 A-spec LM", name);
    }

    [Fact]
    public void Reads_every_car_when_the_blocks_are_different_lengths()
    {
        var db = BuildDatabase(
            ("a-a7r", [0xC5, 0x25, 0xA8, 0x38, 0x6C, 0x73], "Mazda RX-7 A-spec LM"),
            ("h2s2n", [0x94], "Honda S2000"),
            ("gv4rr", [0x52, 0x62], "Volkswagen Golf Rally Car"));

        var info = CarInfo.TryParse(db);

        Assert.NotNull(info);
        Assert.Equal("Mazda RX-7 A-spec LM", info!.DisplayName("a-a7r"));
        Assert.Equal("Honda S2000", info.DisplayName("h2s2n"));
        Assert.Equal("Volkswagen Golf Rally Car", info.DisplayName("gv4rr"));
    }

    [Fact]
    public void Drops_the_marker_byte_some_names_carry()
    {
        // On the disc a name is often stored as 0x7f followed by the text, with
        // the marker counted in the length. It is not part of the name.
        var text = Encoding.ASCII.GetBytes("Viper GTS");
        var block = new List<byte> { 0x94, 0x52, (byte)(text.Length + 1), 0x7F };
        block.AddRange(text);
        block.Add(0);

        var image = new List<byte>(Encoding.ASCII.GetBytes("CAR\0"));
        image.AddRange(BitConverter.GetBytes(1u));
        image.AddRange(BitConverter.GetBytes(Pack("dvpgn")));
        image.AddRange(BitConverter.GetBytes((ushort)16));
        image.AddRange(BitConverter.GetBytes((ushort)0));
        image.AddRange(block);

        var info = CarInfo.TryParse(image.ToArray());

        Assert.NotNull(info);
        Assert.Equal("Viper GTS", info!.DisplayName("dvpgn"));
    }

    [Fact]
    public void An_unknown_code_shows_as_itself()
    {
        var db = BuildDatabase(("h2s2n", [0x94], "Honda S2000"));

        var info = CarInfo.TryParse(db);

        Assert.NotNull(info);
        Assert.Equal("Honda S2000", info!.DisplayName("h2s2n"));   // the database works
        Assert.False(info.TryName("zzzzz", out _));
        Assert.Equal("zzzzz", info.DisplayName("zzzzz"));
    }

    [Fact]
    public void Refuses_something_that_is_not_a_car_database()
    {
        Assert.Null(CarInfo.TryParse([1, 2, 3, 4, 5, 6, 7, 8]));
    }

    [Fact]
    public void Refuses_a_truncated_database_without_throwing()
    {
        var db = BuildDatabase(("h2s2n", [0x94], "Honda S2000"));

        Assert.NotNull(CarInfo.TryParse(db));                       // whole file parses
        Assert.Null(CarInfo.TryParse(db[..(db.Length - 6)]));
    }

    [Fact]
    public void Refuses_a_count_the_file_cannot_hold()
    {
        var image = new List<byte>(Encoding.ASCII.GetBytes("CAR\0"));
        image.AddRange(BitConverter.GetBytes(100000u));

        Assert.Null(CarInfo.TryParse(image.ToArray()));
    }
}
