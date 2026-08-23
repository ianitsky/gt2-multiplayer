using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class TimTests
{
    /// <summary>A TIM with one CLUT and one image block, built field by field.</summary>
    static byte[] Build(uint depth, ushort[] clut, ushort words, ushort rows, byte[] pixels)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x10u);
        w.Write(depth | 8u);
        w.Write((uint)(12 + clut.Length * 2));
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write((ushort)clut.Length); w.Write((ushort)1);
        foreach (var c in clut) w.Write(c);
        w.Write((uint)(12 + pixels.Length));
        w.Write((ushort)0); w.Write((ushort)0);
        w.Write(words); w.Write(rows);
        w.Write(pixels);
        return ms.ToArray();
    }

    static ushort Colour(int r, int g, int b) => (ushort)((r >> 3) | (g >> 3) << 5 | (b >> 3) << 10);

    [Fact]
    public void Decodes_a_4bpp_image_low_nibble_first()
    {
        // Two pixels: index 1 then index 2, in a row of four.
        var clut = new ushort[16];
        clut[1] = Colour(248, 0, 0);
        clut[2] = Colour(0, 248, 0);
        var tim = Build(0, clut, words: 1, rows: 1, pixels: [0x21, 0x00]);

        Assert.True(Tim.TryDecode(tim, out int width, out int height, out var rgba));

        Assert.Equal(4, width);
        Assert.Equal(1, height);
        Assert.Equal([248, 0, 0, 255], rgba[0..4]);
        Assert.Equal([0, 248, 0, 255], rgba[4..8]);
    }

    [Fact]
    public void Decodes_an_8bpp_image()
    {
        var clut = new ushort[256];
        clut[7] = Colour(0, 0, 248);
        var tim = Build(1, clut, words: 1, rows: 1, pixels: [7, 0]);

        Assert.True(Tim.TryDecode(tim, out int width, out int height, out var rgba));

        Assert.Equal(2, width);
        Assert.Equal(1, height);
        Assert.Equal([0, 0, 248, 255], rgba[0..4]);
    }

    [Fact]
    public void Treats_black_with_the_flag_clear_as_transparent()
    {
        var clut = new ushort[16];
        clut[0] = 0;
        clut[1] = Colour(248, 248, 248);
        var tim = Build(0, clut, words: 1, rows: 1, pixels: [0x10, 0x00]);

        Assert.True(Tim.TryDecode(tim, out _, out _, out var rgba));

        Assert.Equal(0, rgba[3]);     // index 0 is see-through
        Assert.Equal(255, rgba[7]);   // index 1 is not
    }

    [Fact]
    public void Refuses_a_depth_it_does_not_handle()
    {
        var tim = Build(2, new ushort[16], words: 1, rows: 1, pixels: [0, 0]);

        Assert.False(Tim.TryDecode(tim, out _, out _, out _));
    }

    [Fact]
    public void Refuses_something_that_is_not_a_tim()
    {
        Assert.False(Tim.TryDecode([1, 2, 3, 4], out _, out _, out _));
    }

    [Fact]
    public void Refuses_a_truncated_tim_without_throwing()
    {
        var clut = new ushort[16];
        clut[1] = Colour(248, 0, 0);
        var tim = Build(0, clut, words: 4, rows: 4, pixels: new byte[32]);

        Assert.True(Tim.TryDecode(tim, out _, out _, out _));       // whole file decodes
        Assert.False(Tim.TryDecode(tim[..(tim.Length - 8)], out _, out _, out _));
    }
}
