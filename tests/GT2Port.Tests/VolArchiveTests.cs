using System.IO.Compression;
using System.Text;
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class VolArchiveTests
{
    const int Sector = 2048;

    /// <summary>One entry to place in a built archive.</summary>
    sealed record Item(string Name, byte[]? Payload, int FirstChild = -1, bool Last = false);

    /// <summary>
    /// Builds a GTFS image the way the disc stores one.
    ///
    /// Two properties of the real archive are reproduced deliberately, because
    /// they are what the reader has to get right. Every offset carries a
    /// non-zero remainder, so a reader that does not round down to the sector
    /// reads the wrong bytes. And every declared size is padded out to whole
    /// sectors, so a file's declared length runs past its content - exactly as
    /// on the disc, where .carcolor declares 54 bytes more than it is given.
    /// </summary>
    static byte[] BuildArchive(IReadOnlyList<Item> entries)
    {
        var files = entries.Where(e => e.Payload is not null).ToList();

        int tableBytes = 0x14 + (files.Count + 1) * 4 + 4;   // + the terminator
        int entryBase = (tableBytes + Sector - 1) / Sector * Sector;
        int dataBase = entryBase + (entries.Count * 32 + Sector - 1) / Sector * Sector;

        // The remainder every offset carries. Any value in 1..2047 does; a
        // round number would let a reader that skips the rounding pass.
        const int Remainder = 500;

        var offsets = new List<uint> { (uint)(dataBase + Remainder) };
        foreach (var f in files)
        {
            int padded = (f.Payload!.Length + Sector - 1) / Sector * Sector;
            offsets.Add(offsets[^1] + (uint)padded);
        }

        var image = new byte[(int)offsets[^1] / Sector * Sector + Sector];
        Encoding.ASCII.GetBytes("GTFS").CopyTo(image, 0);
        for (int i = 0; i < offsets.Count; i++)
            BitConverter.GetBytes(offsets[i]).CopyTo(image, 0x14 + i * 4);
        // The run has to stop increasing so the reader knows where it ends.
        BitConverter.GetBytes(0u).CopyTo(image, 0x14 + offsets.Count * 4);

        int fileIndex = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            int at = entryBase + i * 32;
            ushort value = e.Payload is null ? (ushort)e.FirstChild : (ushort)(++fileIndex);
            BitConverter.GetBytes(value).CopyTo(image, at + 4);
            image[at + 6] = (byte)((e.Payload is null && e.FirstChild >= 0 ? 0x01 : 0x00)
                                   | (e.Last ? 0x80 : 0x00));
            Encoding.ASCII.GetBytes(e.Name).CopyTo(image, at + 7);
            if (e.Payload is not null)
                e.Payload.CopyTo(image, (int)offsets[fileIndex - 1] / Sector * Sector);
        }
        return image;
    }

    static byte[] Gzip(byte[] plain)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(plain);
        return ms.ToArray();
    }

    [Fact]
    public void Reads_a_file_from_the_root()
    {
        var payload = Encoding.ASCII.GetBytes("course table");
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("readme", payload, Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        // Declared length overruns the content by the sector-padding slack, so
        // a raw read is compared by prefix.
        Assert.True(vol.TryRead("readme", out var data));
        Assert.Equal(payload, data[..payload.Length]);
    }

    [Fact]
    public void Reads_a_file_from_a_directory()
    {
        var payload = Encoding.ASCII.GetBytes("a course map");
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("crsmap", null, 2, Last: true),
            new Item("..", null, 0),
            new Item("2p_mountain.tim", payload, Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        Assert.True(vol.TryRead("crsmap/2p_mountain.tim", out var data));
        Assert.Equal(payload, data[..payload.Length]);
    }

    [Fact]
    public void Gunzips_a_member_whose_name_says_so()
    {
        var plain = Encoding.ASCII.GetBytes("the decompressed course map");
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("map.tim.gz", Gzip(plain), Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        Assert.True(vol.TryRead("map.tim.gz", out var data));
        Assert.Equal(plain, data);
    }

    [Fact]
    public void Reads_a_file_that_spans_several_sectors()
    {
        var payload = new byte[5000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7);
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("big", payload, Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        Assert.True(vol.TryRead("big", out var data));
        Assert.Equal(payload, data[..payload.Length]);
    }

    [Fact]
    public void Reads_the_file_after_one_that_did_not_fill_its_last_sector()
    {
        // The rule this pins: a file's start is offsets[v-1], rounded DOWN to
        // the sector that contains it - never up. offsets[v-1] here carries
        // the fixture's non-zero remainder, so it is not itself sector
        // aligned; a reader that forgets to round down lands one sector short
        // of "second" and reads the tail of "first" instead.
        var first = new byte[3000];
        var second = Encoding.ASCII.GetBytes("second file");
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("first", first, Last: false),
            new Item("second", second, Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        Assert.True(vol.TryRead("second", out var data));
        Assert.Equal(second, data[..second.Length]);
    }

    [Fact]
    public void Lists_a_directory_without_its_parent_link()
    {
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("crsmap", null, 2, Last: true),
            new Item("..", null, 0),
            new Item("a.tim", [1], Last: false),
            new Item("b.tim", [2], Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        Assert.Equal(new[] { "a.tim", "b.tim" }, vol.Entries("crsmap"));
    }

    [Fact]
    public void Refuses_a_path_that_is_not_there()
    {
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("readme", [1], Last: true),
        ]);
        using var vol = VolArchive.FromImage(image);

        Assert.True(vol.TryRead("readme", out _));          // the archive works
        Assert.False(vol.TryRead("crsmap/nothing", out _));  // and still says no
    }

    [Fact]
    public void Refuses_an_image_that_is_not_an_archive()
    {
        Assert.Null(VolArchive.FromImageOrNull(new byte[Sector]));
    }

    [Fact]
    public void Refuses_a_truncated_archive_without_throwing()
    {
        var image = BuildArchive([
            new Item("..", null, 0),
            new Item("readme", Encoding.ASCII.GetBytes("hello"), Last: true),
        ]);
        // The fixture's image runs one whole sector past the last file's
        // padded data (the buffer that keeps a rounded-down start from ever
        // reading off the end). Cutting only that trailing sector still
        // leaves the file's single data sector intact, so the cut has to
        // remove two sectors to actually take the data away.
        var cut = image[..(image.Length - 2 * Sector)];
        using var vol = VolArchive.FromImage(cut);

        Assert.False(vol.TryRead("readme", out _));
    }
}
