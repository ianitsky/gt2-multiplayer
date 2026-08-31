using System.Text;

namespace GT2Port.Multiplayer;

/// <summary>
/// Reads .carinfoe, the disc's car database, so the LAN lobby's car picker can
/// show a real name instead of the free-text field it used to take - the disc
/// holds 1110 cars, too many to type. Parses the whole table once at load into
/// a dictionary; nothing here re-scans on lookup. In the same spirit as
/// <see cref="Tim"/>: this reads bytes an archive handed back, so it bounds-
/// checks before every read and never throws, whether the file is truncated,
/// declares an absurd count, or simply does not have a name for a given car -
/// each of those is a normal outcome, not a crash.
/// </summary>
public sealed class CarInfo
{
    const string Alphabet = "-0123456789abcdefghijklmnopqrstuvwxyz";
    const int RecordSize = 8;

    readonly Dictionary<string, string> _names;
    readonly Dictionary<string, Colour[]> _colours;

    public int Count { get; }

    CarInfo(int count, Dictionary<string, string> names, Dictionary<string, Colour[]> colours)
    {
        Count = count;
        _names = names;
        _colours = colours;
    }

    /// <summary>
    /// One paint a car can be delivered in: the letter the game knows it by,
    /// and the swatch shown for it.
    ///
    /// The letter is what a race carries. gt2_ovr3_build_race_block_and_fill_
    /// all_six_entrants writes it, sign-extended, into each entrant at +0x04,
    /// which is the whole of what one machine has to tell another for the cars
    /// to be painted the same. The swatch never leaves the menus - the game
    /// unpacks it as five bits per channel and uses it to draw the little
    /// square, and the paint itself lives in the car's own model.
    /// </summary>
    public readonly record struct Colour(sbyte Letter, ushort Swatch)
    {
        /// <summary>Red, green and blue, each 0-31, as gt2_ovr3 unpacks them.</summary>
        public byte Red => (byte)(Swatch & 0x1F);
        public byte Green => (byte)((Swatch >> 5) & 0x1F);
        public byte Blue => (byte)((Swatch >> 10) & 0x1F);
    }

    /// <summary>
    /// The paints a car comes in, in the game's own order - so an index into
    /// this list is the number the arcade's parameter block wants at +0x16.
    /// Empty for a car this file does not describe, which is the same answer
    /// as a car with no name.
    /// </summary>
    public IReadOnlyList<Colour> Colours(string code) =>
        _colours.TryGetValue(code, out var found) ? found : [];

    /// <summary>
    /// A five-character code back into the packed word the game stores it as -
    /// the inverse of <see cref="TryDecodeCode"/>, and it lives beside it so
    /// the two cannot drift apart. False for anything the format cannot carry:
    /// a code of the wrong length, or a character outside the alphabet.
    /// </summary>
    public static bool TryEncodeCode(string code, out uint packed)
    {
        packed = 0;
        if (code.Length != 5) return false;

        int[] shifts = [24, 18, 12, 6, 0];
        for (int i = 0; i < 5; i++)
        {
            int index = Alphabet.IndexOf(code[i]);
            if (index < 0) return false;
            packed |= (uint)index << shifts[i];
        }
        return true;
    }

    /// <summary>
    /// Loads the car database from the disc archive. Null when there is no
    /// archive, it has no .carinfoe, or the file will not parse - never throws.
    /// </summary>
    public static CarInfo? TryLoad(VolArchive? archive)
    {
        if (archive is null) return null;
        if (!archive.TryRead(".carinfoe", out var data)) return null;
        return TryParse(data);
    }

    /// <summary>Parses a car database already in memory. Internal seam for tests.</summary>
    internal static CarInfo? TryParse(byte[] data)
    {
        if (data is null) return null;

        ReadOnlySpan<byte> span = data;
        int offset = 0;

        if (span.Length < 4 || span[0] != (byte)'C' || span[1] != (byte)'A' || span[2] != (byte)'R' || span[3] != 0)
            return null;
        offset = 4;

        if (!TryU32(span, ref offset, out uint count)) return null;

        // The record table has to actually fit before it is worth reading -
        // this is also what catches an absurd declared count outright.
        long recordTableBytes = (long)count * RecordSize;
        if (recordTableBytes > span.Length - offset) return null;

        int n = (int)count;
        var codes = new string?[n];
        var starts = new int[n];
        var paints = new int[n];

        for (int i = 0; i < n; i++)
        {
            if (!TryU32(span, ref offset, out uint packed)) return null;

            // One word, three fields, and the second half of it is not the
            // spare this used to skip.
            // gt2_main_carinfo_block_and_paint_count_for_car takes the low
            // eighteen bits as the block's offset and the five above them as
            // one less than the number of paints the car comes in, and every
            // reader of a car's colours in the game goes through it.
            if (!TryU32(span, ref offset, out uint field)) return null;

            starts[i] = (int)(field & 0x3FFFFu);
            paints[i] = (int)((field >> 18) & 0x1Fu) + 1;
            codes[i] = TryDecodeCode(packed, out string code) ? code : null;
        }

        var colours = new Dictionary<string, Colour[]>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            if (codes[i] is not { } paintCode) continue;
            if (TryReadColours(data, starts[i], paints[i], out var found))
                colours[paintCode] = found;
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            int start = starts[i];
            // start is eighteen bits masked out of an unsigned word - it can
            // never be negative, only out of range high (review Minor 8).
            if (start > data.Length) continue; // this car's own offset is unusable - no name, nothing else affected

            if (i + 1 < n)
            {
                int end = starts[i + 1];

                // A field block runs to the next record's offset. Two records
                // sharing a block, offsets out of order, or a block cut off
                // before its own trailing NUL each make just this one car's
                // name unreadable - not a reason to distrust the other 1109.
                if (end < start || end > data.Length || end == start || data[end - 1] != 0) continue;

                if (codes[i] is { } code && TryExtractName(data, start, end, out string name))
                    names[code] = name;
            }
            // The last record has no next record to bound its own end, but
            // its start still needs a floor: with none, a corrupted or
            // forged offset here can land inside (or before) an earlier
            // car's own block and TryFindLastBlockEnd will happily walk
            // forward to that car's own terminator, silently attributing
            // its name to this one instead - promoted from "this car has no
            // name" (what a bad offset gets everywhere else, since Important
            // 3 of the previous round) to "this car has someone else's
            // name" (review Minor 4). Require it to land at or past the
            // previous record's own start, same as any in-order block would.
            else if (codes[i] is { } lastCode
                && (i == 0 || start > starts[i - 1])
                && TryFindLastBlockEnd(data, start, out string lastName))
            {
                names[lastCode] = lastName;
            }
        }

        return new CarInfo(n, names, colours);
    }

    /// <summary>
    /// Reads one car's paints out of its block, which begins with them:
    /// <paramref name="count"/> swatches as halfwords, then the same number of
    /// letters as bytes, and the car's name after those. The two run in step -
    /// gt2_ovr3 reads swatch i at <c>block + i * 2</c> and letter i at
    /// <c>block + count * 2 + i</c> - so they are paired here rather than kept
    /// apart.
    ///
    /// False, and no colours for this car, when the block does not fit: the
    /// same answer a missing name gets, and for the same reason - one car's
    /// unusable offset says nothing about the other 1109.
    /// </summary>
    static bool TryReadColours(byte[] data, int start, int count, out Colour[] colours)
    {
        colours = [];
        if (count <= 0 || start < 0) return false;

        long end = (long)start + count * 3;
        if (end > data.Length) return false;

        var found = new Colour[count];
        for (int i = 0; i < count; i++)
        {
            ushort swatch = (ushort)(data[start + i * 2] | (data[start + i * 2 + 1] << 8));
            found[i] = new Colour((sbyte)data[start + count * 2 + i], swatch);
        }

        colours = found;
        return true;
    }

    /// <summary>Looks up a car's display name. False when the code is unknown or its name will not parse.</summary>
    public bool TryName(string code, out string name)
    {
        if (_names.TryGetValue(code, out var found))
        {
            name = found;
            return true;
        }
        name = "";
        return false;
    }

    /// <summary>
    /// The name to show for a car code. An unknown code returns itself, mirroring
    /// <see cref="CourseTable.DisplayName"/>.
    /// </summary>
    public string DisplayName(string code) => TryName(code, out var name) ? name : code;

    /// <summary>
    /// Decodes a packed code: five characters, six bits each, most significant
    /// first, over <see cref="Alphabet"/>. False if any character's index falls
    /// outside the alphabet - not a code this format can produce.
    /// </summary>
    static bool TryDecodeCode(uint packed, out string code)
    {
        Span<char> chars = stackalloc char[5];
        for (int i = 0; i < 5; i++)
        {
            int index = (int)((packed >> ((4 - i) * 6)) & 0x3F);
            if (index >= Alphabet.Length) { code = ""; return false; }
            chars[i] = Alphabet[index];
        }
        code = new string(chars);
        return true;
    }

    /// <summary>
    /// Finds the display name within one field block: strip trailing NULs,
    /// then scan backwards for the first position whose byte value equals the
    /// number of bytes remaining after it - the length prefix of the last
    /// counted string in the block. Everything after that byte is the name,
    /// dropping any byte outside printable ASCII (some names carry a 0x7f
    /// marker counted in the length but not part of the text). False when no
    /// such position exists - the block simply has no name.
    /// </summary>
    static bool TryExtractName(byte[] data, int start, int end, out string name)
    {
        name = "";

        int trimmedEnd = end;
        while (trimmedEnd > start && data[trimmedEnd - 1] == 0) trimmedEnd--;
        int length = trimmedEnd - start;
        if (length <= 0) return false;

        for (int p = length - 1; p >= 0; p--)
        {
            byte b = data[start + p];
            int remaining = length - p - 1;
            if (b == 0 || b != remaining) continue;

            var sb = new StringBuilder();
            for (int q = start + p + 1; q < trimmedEnd; q++)
            {
                byte c = data[q];
                if (c is >= 32 and <= 126) sb.Append((char)c);
            }
            name = sb.ToString();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds the last record's field block on its own terms, since it has no
    /// next record to bound it and <c>data.Length</c> is not trustworthy for
    /// that job: a raw <see cref="VolArchive"/> read runs past the file's
    /// real content by up to a sector of whatever the disc happened to have
    /// there, and folding that into the search lets leftover bytes further
    /// out mask the real name or masquerade as one - confirmed against the
    /// real disc, where car 1110's declared block runs 1.6KB into a stretch
    /// of unrelated data and the wrong "name" comes back before the reader
    /// ever reaches the real one at the front.
    ///
    /// Tries each candidate end past <paramref name="start"/> in increasing
    /// order and takes the first that both lands on a trailing NUL and
    /// yields a name via <see cref="TryExtractName"/>. This is a heuristic,
    /// not a guarantee: nothing stops a name's own bytes from containing a
    /// NUL followed by something that also happens to parse as a valid
    /// length-counted string, in which case this returns that false,
    /// earlier block instead of the real one. Measured against the real
    /// <c>.carinfoe</c>'s 1109 bounded blocks (the ones with a following
    /// record to bound them, so the correct extraction is independently
    /// known): 571 contain a NUL before their own terminator, and for 5 of
    /// the full 1109 - a rate of about five in a thousand - running this
    /// same scan over their bytes would return the wrong string. This scan
    /// is only ever actually applied to the one record with no next record
    /// to bound it; on this disc that record (car 1110) is not one of the
    /// five, so today's output happens to be correct - by measurement, not
    /// by an invariant this scan can rely on. False if no candidate boundary
    /// exists at all before the end of the file - this car simply has no
    /// name, exactly like any other unresolvable block.
    /// </summary>
    static bool TryFindLastBlockEnd(byte[] data, int start, out string name)
    {
        for (int end = start + 1; end <= data.Length; end++)
        {
            if (data[end - 1] != 0) continue;
            if (TryExtractName(data, start, end, out name)) return true;
        }
        name = "";
        return false;
    }

    static bool TryU32(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        if (data.Length - offset < 4) { value = 0; return false; }
        value = BitConverter.ToUInt32(data.Slice(offset, 4));
        offset += 4;
        return true;
    }
}
