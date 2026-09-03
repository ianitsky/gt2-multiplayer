using GT2Port;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// Finding a picture in video memory by the picture itself.
///
/// Where the game puts the title sheet is the game's business, so the tile is
/// its own needle. What has to hold is that the needle is found where it is,
/// not found where it is not, and never half-found - a tile that matches on
/// its first row and differs on its last is a different picture, and writing
/// over it would corrupt whatever it really was.
/// </summary>
public class TitleLabelTests
{
    const int Pitch = 64;
    const int Height = 32;
    const int Words = 4;
    const int Rows = 3;

    static ushort[] Tile() => [11, 12, 13, 14, 21, 22, 23, 24, 31, 32, 33, 34];

    static ushort[] EmptyVram()
    {
        var vram = new ushort[Pitch * Height];
        // Not zero: zero everywhere would let a needle of zeroes match by
        // accident, which is the one thing a test here must not do.
        for (int i = 0; i < vram.Length; i++) vram[i] = 0xFFFF;
        return vram;
    }

    static void Plant(ushort[] vram, ushort[] tile, int wx, int wy)
    {
        for (int row = 0; row < Rows; row++)
            for (int w = 0; w < Words; w++)
                vram[(wy + row) * Pitch + wx + w] = tile[row * Words + w];
    }

    [Fact]
    public void Finds_the_tile_where_it_was_planted()
    {
        var vram = EmptyVram();
        Plant(vram, Tile(), 17, 9);

        Assert.True(TitleLabel.TryFind(vram, Pitch, Height, Tile(), Words, Rows,
            out int wx, out int wy));

        Assert.Equal(17, wx);
        Assert.Equal(9, wy);
    }

    [Fact]
    public void Finds_nothing_when_it_is_not_there()
    {
        Assert.False(TitleLabel.TryFind(EmptyVram(), Pitch, Height, Tile(), Words, Rows,
            out int wx, out int wy));

        Assert.Equal(-1, wx);
        Assert.Equal(-1, wy);
    }

    /// <summary>
    /// The first word is what narrows the sweep, so a picture that shares it
    /// and nothing else is exactly the case that would slip through a search
    /// that stopped there.
    /// </summary>
    [Fact]
    public void A_tile_that_only_starts_the_same_is_not_a_match()
    {
        var vram = EmptyVram();
        var nearly = Tile();
        nearly[^1] = 0x1234;
        Plant(vram, nearly, 5, 5);

        Assert.False(TitleLabel.TryFind(vram, Pitch, Height, Tile(), Words, Rows,
            out _, out _));
    }

    /// <summary>
    /// A tile whose rows are each present but on the wrong lines is not the
    /// tile - which is what a search comparing rows without their pitch would
    /// happily accept.
    /// </summary>
    [Fact]
    public void Rows_have_to_line_up()
    {
        var vram = EmptyVram();
        var tile = Tile();
        for (int row = 0; row < Rows; row++)
            for (int w = 0; w < Words; w++)
                vram[(3 + row * 2) * Pitch + 8 + w] = tile[row * Words + w];

        Assert.False(TitleLabel.TryFind(vram, Pitch, Height, tile, Words, Rows,
            out _, out _));
    }

    [Fact]
    public void A_tile_that_would_run_off_the_edge_is_refused()
    {
        var vram = EmptyVram();

        Assert.False(TitleLabel.Matches(vram, Pitch, Height, Tile(), Words, Rows,
            Pitch - Words + 1, 0));
        Assert.False(TitleLabel.Matches(vram, Pitch, Height, Tile(), Words, Rows,
            0, Height - Rows + 1));
        Assert.False(TitleLabel.Matches(vram, Pitch, Height, Tile(), Words, Rows,
            -1, 0));
    }

    /// <summary>
    /// And the pair the port actually ships is a pair: the same shape, and not
    /// the same picture. A generator that wrote the tile out twice unchanged
    /// would leave a patch that finds its needle and changes nothing.
    /// </summary>
    [Fact]
    public void The_shipped_tile_differs_from_the_one_it_replaces()
    {
        string path = GameFiles.Find("config", "title-multiplayer.bin");
        Assert.True(File.Exists(path), $"{path} should ship with the port");

        var blob = File.ReadAllBytes(path);
        Assert.Equal((byte)'G', blob[0]);
        Assert.Equal((byte)'2', blob[1]);
        Assert.Equal((byte)'T', blob[2]);
        Assert.Equal((byte)'L', blob[3]);

        int words = blob[6] | (blob[7] << 8);
        int rows = blob[8] | (blob[9] << 8);
        Assert.True(words > 0 && rows > 0);

        int half = words * rows * 2;
        Assert.Equal(10 + half * 2, blob.Length);

        Assert.False(blob.AsSpan(10, half).SequenceEqual(blob.AsSpan(10 + half, half)));
    }
}
