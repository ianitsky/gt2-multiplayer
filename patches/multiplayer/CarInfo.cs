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

    public int Count { get; }

    CarInfo(int count, Dictionary<string, string> names)
    {
        Count = count;
        _names = names;
    }

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

        for (int i = 0; i < n; i++)
        {
            if (!TryU32(span, ref offset, out uint packed)) return null;
            if (!TryU16(span, ref offset, out ushort blockOffset)) return null;
            if (!TryU16(span, ref offset, out _)) return null; // unused field

            starts[i] = blockOffset;
            codes[i] = TryDecodeCode(packed, out string code) ? code : null;
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            int start = starts[i];
            // start comes from TryU16, an unsigned read - it can never be
            // negative, only out of range high (review Minor 8).
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

        return new CarInfo(n, names);
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

    static bool TryU16(ReadOnlySpan<byte> data, ref int offset, out ushort value)
    {
        if (data.Length - offset < 2) { value = 0; return false; }
        value = BitConverter.ToUInt16(data.Slice(offset, 2));
        offset += 2;
        return true;
    }
}
