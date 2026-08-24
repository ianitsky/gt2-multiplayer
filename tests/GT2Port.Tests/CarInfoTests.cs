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

    /// <summary>
    /// A database holding exactly the given codes, each named "Car " + code.
    /// Shared seam for sibling test files that just need CarInfo to answer
    /// "is this a real car" without caring about the wire format.
    /// </summary>
    internal static CarInfo BuildDatabaseFor(params string[] codes)
    {
        var cars = codes.Select(code => (code, (byte[])[0x00], "Car " + code)).ToArray();
        return CarInfo.TryParse(BuildDatabase(cars))!;
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
    public void Refuses_a_database_truncated_before_its_own_car_count_fits()
    {
        // Review Minor 7: db[..15] cut into the record table itself and hit
        // the exact same "record table does not fit" guard that
        // Refuses_a_count_the_file_cannot_hold already covers. Cut inside
        // the count field instead, at offset 4-8, so this reaches the
        // earlier "the count itself was not readable" guard - a different
        // branch, not a rewrite of the same one.
        var db = BuildDatabase(("h2s2n", [0x94], "Honda S2000"));

        Assert.NotNull(CarInfo.TryParse(db));                       // whole file parses
        Assert.Null(CarInfo.TryParse(db[..6]));                     // cuts into the count field itself
    }

    [Fact]
    public void Refuses_a_count_the_file_cannot_hold()
    {
        var image = new List<byte>(Encoding.ASCII.GetBytes("CAR\0"));
        image.AddRange(BitConverter.GetBytes(100000u));

        Assert.Null(CarInfo.TryParse(image.ToArray()));
    }

    // ---- Review Important 3: one bad field block must not cost every name ----

    [Fact]
    public void A_field_block_cut_off_before_its_own_trailing_NUL_loses_only_that_car()
    {
        // The record table is fully intact - only the field block itself is
        // truncated, mid-name, with no trailing NUL anywhere left to find.
        var db = BuildDatabase(("h2s2n", [0x94], "Honda S2000"));

        var info = CarInfo.TryParse(db[..(db.Length - 6)]);

        Assert.NotNull(info);                                        // no longer a whole-file rejection
        Assert.False(info!.TryName("h2s2n", out _));                 // this car alone has no name
    }

    [Fact]
    public void Two_records_pointing_at_the_same_block_lose_only_that_block_not_the_database()
    {
        var db = BuildDatabase(
            ("a-a7r", [0xC5, 0x25, 0xA8], "Mazda RX-7 A-spec LM"),
            ("h2s2n", [0x94], "Honda S2000"),
            ("gv4rr", [0x52, 0x62], "Volkswagen Golf Rally Car"));

        // Corrupt h2s2n's own record so it points at a-a7r's start instead of
        // its own. a-a7r's own block now runs from its start to its own
        // start - the "end == start" case - so a-a7r alone loses its name;
        // gv4rr never shared a block with anyone and is untouched.
        var image = db.ToArray();
        int h2s2nRecordOffset = 8 + 1 * 8; // header(8) + record 0
        ushort a7rStart = BitConverter.ToUInt16(db, 8 + 4);
        BitConverter.GetBytes(a7rStart).CopyTo(image, h2s2nRecordOffset + 4);

        var info = CarInfo.TryParse(image);

        Assert.NotNull(info);                                        // the database as a whole still parses
        Assert.False(info!.TryName("a-a7r", out _));                 // the corrupted record's own name is unreachable
        Assert.Equal("Volkswagen Golf Rally Car", info.DisplayName("gv4rr"));
    }

    [Fact]
    public void Records_out_of_ascending_order_lose_only_that_car()
    {
        var db = BuildDatabase(
            ("a-a7r", [0xC5, 0x25, 0xA8], "Mazda RX-7 A-spec LM"),
            ("h2s2n", [0x94], "Honda S2000"),
            ("gv4rr", [0x52, 0x62], "Volkswagen Golf Rally Car"));

        // Swap h2s2n's and a-a7r's declared offsets, so record 0 (a-a7r) now
        // points past record 1's (h2s2n) start - descending, not ascending.
        var image = db.ToArray();
        ushort a7rStart = BitConverter.ToUInt16(db, 8 + 4);
        ushort h2s2nStart = BitConverter.ToUInt16(db, 16 + 4);
        BitConverter.GetBytes(h2s2nStart).CopyTo(image, 8 + 4);
        BitConverter.GetBytes(a7rStart).CopyTo(image, 16 + 4);

        var info = CarInfo.TryParse(image);

        Assert.NotNull(info);
        Assert.False(info!.TryName("a-a7r", out _));                // the corrupted record alone loses its name
        Assert.Equal("Volkswagen Golf Rally Car", info.DisplayName("gv4rr"));
    }

    // ---- Review Important 4: the last block must not read past its own content ----

    [Fact]
    public void The_last_car_resolves_even_when_the_buffer_runs_on_past_it()
    {
        // VolArchive's raw reads overrun the real content (its own doc says
        // so) - simulate that overrun with non-NUL bytes appended after a
        // well-formed database, standing in for whatever the disc's next
        // member happened to leave behind.
        var db = BuildDatabase(
            ("h2s2n", [0x94], "Honda S2000"),
            ("gv4rr", [0x52, 0x62], "Volkswagen Golf Rally Car"));

        var overrun = new byte[db.Length + 40];
        db.CopyTo(overrun, 0);
        var rng = new Random(1);
        for (int i = db.Length; i < overrun.Length; i++)
        {
            byte b;
            do { b = (byte)rng.Next(1, 256); } while (b == 0); // never a NUL - that's the point
            overrun[i] = b;
        }

        var info = CarInfo.TryParse(overrun);

        Assert.NotNull(info);
        Assert.Equal("Volkswagen Golf Rally Car", info!.DisplayName("gv4rr"));
    }

    [Fact]
    public void A_last_block_with_no_reachable_terminator_has_no_name_but_does_not_fail_the_file()
    {
        var db = BuildDatabase(
            ("h2s2n", [0x94], "Honda S2000"),
            ("gv4rr", [0x52, 0x62], "Volkswagen Golf Rally Car"));

        // No trailing NUL anywhere left in the buffer at all.
        var overrun = new byte[db.Length + 10];
        db.CopyTo(overrun, 0);
        for (int i = db.Length; i < overrun.Length; i++) overrun[i] = 0xFF;
        // Also blank out gv4rr's own trailing NUL so nothing before the
        // appended garbage can terminate it either.
        overrun[db.Length - 1] = 0xFF;

        var info = CarInfo.TryParse(overrun);

        Assert.NotNull(info);
        Assert.False(info!.TryName("gv4rr", out _));
        Assert.Equal("Honda S2000", info.DisplayName("h2s2n"));      // an earlier car is unaffected
    }

    [Fact]
    public void The_last_car_resolves_to_its_own_name_past_an_appended_counted_string()
    {
        // Review Important 3: neither existing overrun test pins the bug its
        // fix was written for. Appending only non-NUL bytes leaves exactly
        // one terminator in the buffer, and stripping every terminator
        // leaves none - both are red against the unfixed (whole-file
        // rejecting) code, but neither exercises a *second* well-formed
        // candidate. The naive alternative fix - scan backward from
        // data.Length for the last NUL - passes both of those and still
        // returns the wrong name for car 1110 on the real disc.
        //
        // A NUL-terminated counted string appended after a well-formed
        // database is exactly that second candidate: 0x05 "ABCDE" 0x00 is a
        // valid length-counted block in its own right, sitting after the
        // real last block's own terminator. The forward scan this class
        // actually uses finds the real terminator first and never looks
        // past it; scanning backward from the end finds the appended one
        // instead and returns "ABCDE".
        var db = BuildDatabase(
            ("h2s2n", [0x94], "Honda S2000"),
            ("gv4rr", [0x52, 0x62], "Volkswagen Golf Rally Car"));

        byte[] appendedCountedString = [0x05, (byte)'A', (byte)'B', (byte)'C', (byte)'D', (byte)'E', 0x00];
        var withJunk = new byte[db.Length + appendedCountedString.Length];
        db.CopyTo(withJunk, 0);
        appendedCountedString.CopyTo(withJunk, db.Length);

        var info = CarInfo.TryParse(withJunk);

        Assert.NotNull(info);
        Assert.Equal("Volkswagen Golf Rally Car", info!.DisplayName("gv4rr"));
    }

    // ---- Review Minor 4: the last record's start needs a floor too ----

    [Fact]
    public void The_last_record_has_no_name_when_its_start_points_before_the_previous_record()
    {
        var db = BuildDatabase(
            ("a-a7r", [0xC5, 0x25, 0xA8], "Mazda RX-7 A-spec LM"),
            ("h2s2n", [0x94], "Honda S2000"),
            ("gv4rr", [0x52, 0x62], "Volkswagen Golf Rally Car"));

        // Rewrite gv4rr's own record (the last one) so its declared start is
        // a-a7r's start instead of its own. With no floor, the forward scan
        // from there reaches a-a7r's own terminator first and silently
        // returns "Mazda RX-7 A-spec LM" as gv4rr's name - another car's
        // name, not "no name" - exactly the shape Important 3 of the
        // previous round closed for every record except the last.
        var image = db.ToArray();
        ushort a7rStart = BitConverter.ToUInt16(db, 8 + 4);
        int gv4rrRecordOffset = 8 + 2 * 8; // header(8) + record 0 + record 1
        BitConverter.GetBytes(a7rStart).CopyTo(image, gv4rrRecordOffset + 4);

        var info = CarInfo.TryParse(image);

        Assert.NotNull(info);                                        // the database as a whole still parses
        Assert.False(info!.TryName("gv4rr", out _));                 // not "Mazda RX-7 A-spec LM"
        Assert.Equal("Mazda RX-7 A-spec LM", info.DisplayName("a-a7r")); // the untouched earlier record is unaffected
    }
}
