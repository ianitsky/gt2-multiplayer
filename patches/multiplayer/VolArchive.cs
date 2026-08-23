using System.IO.Compression;
using System.Text;
using RecompOne.Runtime.Cdrom;

namespace GT2Port.Multiplayer;

/// <summary>
/// Reads files out of GT2.VOL, the GTFS archive holding every asset the game
/// loads - course map pictures among them. Nothing in the port needed this
/// before: overlays come from GT2.OVL, and the game reaches everything else
/// through its own GTFS code, so listing or reading an entry ourselves means
/// parsing the format directly. Ported from <c>tools/gt2vol.py</c>, the
/// reference reader whose layout notes were verified against the real disc.
/// Offsets in the header are absolute byte positions from byte 0 of the
/// archive - there is no separate data base to locate. A file with directory
/// value <c>v</c> starts at the sector containing <c>offsets[v-1]</c>, rounded
/// DOWN, and its declared length is <c>offsets[v] - offsets[v-1]</c>, which
/// runs past the file's actual content (the disc pads to a sector boundary);
/// a raw read's tail is slack, while a gzipped member is unaffected because
/// <see cref="System.IO.Compression.GZipStream"/> stops at the end of the one
/// member it decompresses.
/// </summary>
public sealed class VolArchive : IDisposable
{
    const int Sector = 2048;
    const int EntrySize = 32;
    const byte DirectoryFlag = 0x01;
    const byte LastFlag = 0x80;

    readonly Func<int, byte[]?> _readSector;
    readonly DiscFs? _disc;
    readonly uint[] _offsets;
    readonly int _entryBase;

    readonly Dictionary<int, Entry> _entryCache = [];
    readonly Dictionary<string, List<string>> _dirCache = [];
    bool _disposed;

    readonly record struct Entry(string Name, ushort Value, byte Flags);

    VolArchive(Func<int, byte[]?> readSector, DiscFs? disc, uint[] offsets, int entryBase)
    {
        _readSector = readSector;
        _disc = disc;
        _offsets = offsets;
        _entryBase = entryBase;
    }

    /// <summary>
    /// Opens GT2.VOL on the disc at <paramref name="discPath"/>. Null when the
    /// disc cannot be opened or carries no GT2.VOL - never throws, since the
    /// caller is deciding whether to offer the picture-based course grid at
    /// all and a missing archive just means falling back gracefully.
    /// </summary>
    public static VolArchive? TryOpen(string discPath)
    {
        DiscFs? disc = null;
        try
        {
            disc = DiscFs.Open(discPath);
            if (!disc.Locate("GT2.VOL", out int lba, out _))
            {
                disc.Dispose();
                return null;
            }

            var archive = Build(index => ReadDiscSector(disc, lba, index), disc);
            if (archive is null) disc.Dispose();
            return archive;
        }
        catch
        {
            disc?.Dispose();
            return null;
        }
    }

    static byte[]? ReadDiscSector(DiscFs disc, int volLba, int index)
    {
        try
        {
            return disc.ReadSector(volLba + index);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds an archive over bytes already in memory, for tests that pin the
    /// format down without a disc image on hand. Throws only when the bytes
    /// are not a GTFS archive at all - anything short of that (truncation,
    /// missing entries) is a normal <see cref="TryRead"/>/<see cref="Entries"/>
    /// failure instead.
    /// </summary>
    internal static VolArchive FromImage(byte[] image) =>
        FromImageOrNull(image) ?? throw new InvalidDataException("image is not a GTFS archive");

    internal static VolArchive? FromImageOrNull(byte[] image)
    {
        try
        {
            return Build(index => ReadImageSector(image, index), null);
        }
        catch
        {
            return null;
        }
    }

    static byte[]? ReadImageSector(byte[] image, int index)
    {
        long start = (long)index * Sector;
        if (start < 0 || start >= image.Length) return null;
        var buf = new byte[Sector];
        int n = (int)Math.Min(Sector, image.Length - start);
        Array.Copy(image, start, buf, 0, n);
        return buf;
    }

    static VolArchive? Build(Func<int, byte[]?> readSector, DiscFs? disc)
    {
        var header = readSector(0);
        if (header is null || header.Length < 0x14) return null;
        if (header[0] != (byte)'G' || header[1] != (byte)'T' || header[2] != (byte)'F' || header[3] != (byte)'S')
            return null;

        // Read the offset table in blocks rather than assuming a length: the
        // run ends at the first value that does not increase, which is also
        // where the table gets padded out to the entries.
        var offsets = new List<uint>();
        long pos = 0x14;
        const int BlockBytes = Sector * 8;
        while (true)
        {
            var block = ReadRaw(readSector, pos, BlockBytes);
            if (block is null || block.Length < 4) break;

            bool stopped = false;
            int i = 0;
            for (; i + 4 <= block.Length; i += 4)
            {
                uint value = BitConverter.ToUInt32(block, i);
                if (offsets.Count > 0 && value <= offsets[^1]) { stopped = true; break; }
                offsets.Add(value);
            }
            pos += i;
            if (stopped || block.Length < BlockBytes) break;
        }

        if (offsets.Count == 0) return null;

        int tableBytes = 0x14 + offsets.Count * 4;
        int entryBase = (tableBytes + Sector - 1) / Sector * Sector;

        return new VolArchive(readSector, disc, [.. offsets], entryBase);
    }

    /// <summary>
    /// Reads a slash-separated <paramref name="path"/> out of the archive. A
    /// member whose name ends ".gz" is gunzipped; anything else comes back
    /// raw. False for anything missing, malformed, or too short to actually
    /// hold the bytes it claims to - never throws.
    /// </summary>
    public bool TryRead(string path, out byte[] data)
    {
        data = [];
        if (_disposed || string.IsNullOrEmpty(path)) return false;

        if (!TryResolve(path, out var found) || (found.Flags & DirectoryFlag) != 0) return false;

        ushort value = found.Value;
        if (value < 1 || value >= _offsets.Length) return false;

        // Offsets are absolute byte positions from byte 0 of the archive. A
        // file starts at the sector CONTAINING offsets[v-1] - rounded down,
        // never up - and its declared length is the raw distance to the next
        // offset, which runs past the actual content (the disc pads to a
        // sector boundary). That overrun is why a raw read has to be trimmed
        // by the caller; a gzipped member is unaffected since GZipStream
        // stops at the end of its one member regardless of trailing slack.
        uint rawStart = _offsets[value - 1];
        uint end = _offsets[value];
        if (end < rawStart) return false;
        long start = FloorToSector(rawStart);
        long length = end - rawStart;

        var bytes = ReadExact(start, length);
        if (bytes is null) return false;

        if (path.EndsWith(".gz", StringComparison.Ordinal))
        {
            try
            {
                return TryGunzip(bytes, out data);
            }
            catch
            {
                return false;
            }
        }

        data = bytes;
        return true;
    }

    static bool TryGunzip(byte[] compressed, out byte[] plain)
    {
        using var input = new MemoryStream(compressed);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        plain = output.ToArray();
        return true;
    }

    /// <summary>
    /// Names in one directory, "..\" excluded. Empty for a path that is not a
    /// directory. Cached: a directory's listing never changes once the archive
    /// is open, but file contents are not cached here - the caller decides
    /// what to keep.
    /// </summary>
    public IReadOnlyList<string> Entries(string directory)
    {
        if (_disposed) return [];
        if (_dirCache.TryGetValue(directory, out var cached)) return cached;

        int listingStart;
        if (string.IsNullOrEmpty(directory))
        {
            listingStart = 0;
        }
        else
        {
            if (!TryResolve(directory, out var found) || (found.Flags & DirectoryFlag) == 0) return [];
            listingStart = found.Value;
        }

        var listing = GetListing(listingStart);
        if (listing is null) return [];

        var names = listing.Where(e => e.Name != "..").Select(e => e.Name).ToList();
        _dirCache[directory] = names;
        return names;
    }

    /// <summary>
    /// Walks a slash-separated path from the root listing (entry 0), one
    /// component at a time. Every component but the last must be a directory;
    /// the last may be either, and its own entry is returned.
    /// </summary>
    bool TryResolve(string path, out Entry found)
    {
        found = default;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        int listingStart = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            var listing = GetListing(listingStart);
            if (listing is null) return false;

            Entry? match = null;
            foreach (var e in listing)
            {
                if (e.Name == parts[i]) { match = e; break; }
            }
            if (match is null) return false;

            if (i == parts.Length - 1)
            {
                found = match.Value;
                return true;
            }

            if ((match.Value.Flags & DirectoryFlag) == 0) return false;
            listingStart = match.Value.Value;
        }

        return false;
    }

    static long FloorToSector(uint value) => (long)value / Sector * Sector;

    /// <summary>
    /// Upper bound on entries read for a single directory listing, so a
    /// malformed archive (one whose last entry never carries the 0x80 flag)
    /// cannot spin the reader forever.
    /// </summary>
    const int MaxListingEntries = 65536;

    List<Entry>? GetListing(int start)
    {
        var result = new List<Entry>();
        int index = start;
        for (int i = 0; i < MaxListingEntries; i++)
        {
            if (!TryGetEntry(index, out var entry)) return null;
            result.Add(entry);
            if ((entry.Flags & LastFlag) != 0) return result;
            index++;
        }
        return null;
    }

    bool TryGetEntry(int index, out Entry entry)
    {
        if (_entryCache.TryGetValue(index, out entry)) return true;

        var raw = ReadExact((long)_entryBase + (long)index * EntrySize, EntrySize);
        if (raw is null) { entry = default; return false; }

        ushort value = BitConverter.ToUInt16(raw, 4);
        byte flags = raw[6];
        int nameLen = Array.IndexOf(raw, (byte)0, 7, 25);
        if (nameLen < 0) nameLen = 32;
        string name = Encoding.ASCII.GetString(raw, 7, nameLen - 7);

        entry = new Entry(name, value, flags);
        _entryCache[index] = entry;
        return true;
    }

    /// <summary>
    /// Reads exactly <paramref name="length"/> bytes starting at a byte offset
    /// into the archive, or null if that much data is not actually there -
    /// the truncated-archive case a real disc read or a cut-down test image
    /// can both produce.
    /// </summary>
    byte[]? ReadExact(long offset, long length)
    {
        var data = ReadRaw(_readSector, offset, length);
        return data is not null && data.Length == length ? data : null;
    }

    static byte[]? ReadRaw(Func<int, byte[]?> readSector, long offset, long length)
    {
        if (length <= 0) return [];
        if (length > int.MaxValue) return null;

        var result = new byte[length];
        long done = 0;
        long pos = offset;
        while (done < length)
        {
            int sectorIndex = (int)(pos / Sector);
            int sectorOffset = (int)(pos % Sector);
            var sector = readSector(sectorIndex);
            if (sector is null) return done == 0 ? null : result[..(int)done];

            int available = sector.Length - sectorOffset;
            if (available <= 0) return done == 0 ? null : result[..(int)done];

            int n = (int)Math.Min(length - done, available);
            Array.Copy(sector, sectorOffset, result, done, n);
            done += n;
            pos += n;
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _entryCache.Clear();
        _dirCache.Clear();
        _disc?.Dispose();
    }
}
