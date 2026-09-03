using RecompOne.Runtime;
using RecompOne.Runtime.Hle;

namespace GT2Port;

/// <summary>
/// Renames the title menu's second item, without touching anybody's disc.
///
/// The label is pixels. arcade/title_item.tim.gz is a 4bpp sheet holding every
/// title-menu label in every language, and the second item's tile is 26 words
/// by 22 rows of it. tools/gen_title_label.py cuts that tile out of the disc
/// twice - as it is, and as it should read - and ships the pair in
/// config/title-multiplayer.bin.
///
/// This finds the first in video memory and writes the second over it.
///
/// **Found rather than addressed**, because where the sheet lands is the
/// game's business and it has never promised to land twice in the same place.
/// The tile is its own needle: 572 words that appear nowhere else. That also
/// makes this idempotent for free - once written, the needle is gone and there
/// is nothing left to match, so it does not write again. And if the game
/// reloads the sheet, the needle comes back and so does the rename.
///
/// **Not swapped at the read**, which would be the obvious place. The title
/// menu's files do not pass gt2_main_vol_get_file_data_sector_offset at all -
/// a traced boot that reached the menu logged exactly one read, and it was the
/// sound bank. Whatever loads this sheet, it is not the path this port can
/// see, so the picture is caught where it certainly is: in memory, drawn.
/// </summary>
public static class TitleLabel
{
    /// <summary>The file's own name for itself, and what it holds.</summary>
    static readonly byte[] Magic = "G2TL"u8.ToArray();
    const ushort Version = 1;

    /// <summary>
    /// How often to go looking, in frames. A full sweep of video memory is
    /// half a million words; at the console's frame rate, twice a second is
    /// far more often than a menu appears and far less often than it would
    /// cost anything.
    /// </summary>
    const int EveryFrames = 30;

    /// <summary>Off when GT2_TITLE_LABEL says so, for seeing the original.</summary>
    static readonly bool Wanted =
        Environment.GetEnvironmentVariable("GT2_TITLE_LABEL") is not "0";

    static ushort[]? _before;
    static ushort[]? _after;
    static int _words;
    static int _rows;
    static bool _loaded;
    static bool _complained;

    static int _countdown;
    static int _foundX = -1;
    static int _foundY = -1;
    static int _written;

    static ushort[]? _vram;

    /// <summary>
    /// Reads the pair of tiles. Returns false when there is nothing to do,
    /// which is not an error: a copy of the port shipped without the file is
    /// a copy that leaves the menu alone.
    /// </summary>
    static bool Load()
    {
        if (_loaded) return _before is not null;
        _loaded = true;

        string path = GameFiles.Find("config", "title-multiplayer.bin");
        if (!File.Exists(path)) return false;

        byte[] blob;
        try { blob = File.ReadAllBytes(path); }
        catch (IOException) { return false; }

        if (blob.Length < 10) return Broken(path, "it is too short to be one");
        for (int i = 0; i < Magic.Length; i++)
            if (blob[i] != Magic[i]) return Broken(path, "it does not start with G2TL");

        ushort version = (ushort)(blob[4] | (blob[5] << 8));
        if (version != Version) return Broken(path, $"it is version {version}, not {Version}");

        _words = blob[6] | (blob[7] << 8);
        _rows = blob[8] | (blob[9] << 8);
        if (_words <= 0 || _rows <= 0) return Broken(path, "it describes an empty tile");

        int need = 10 + _words * _rows * 2 * 2;
        if (blob.Length < need)
            return Broken(path, $"it is {blob.Length} bytes and the tile needs {need}");

        _before = new ushort[_words * _rows];
        _after = new ushort[_words * _rows];
        Buffer.BlockCopy(blob, 10, _before, 0, _before.Length * 2);
        Buffer.BlockCopy(blob, 10 + _before.Length * 2, _after, 0, _after.Length * 2);
        return true;
    }

    static bool Broken(string path, string why)
    {
        if (!_complained)
        {
            _complained = true;
            Console.Error.WriteLine($"[title] {path} was ignored - {why}");
        }
        _before = null;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="tile"/> sits at (<paramref name="wx"/>,
    /// <paramref name="wy"/>) in a picture <paramref name="pitch"/> words
    /// wide.
    /// </summary>
    internal static bool Matches(
        ReadOnlySpan<ushort> vram, int pitch, int height,
        ReadOnlySpan<ushort> tile, int words, int rows, int wx, int wy)
    {
        if (wx < 0 || wy < 0 || wx + words > pitch || wy + rows > height) return false;

        for (int row = 0; row < rows; row++)
        {
            int at = (wy + row) * pitch + wx;
            for (int w = 0; w < words; w++)
                if (vram[at + w] != tile[row * words + w]) return false;
        }
        return true;
    }

    /// <summary>
    /// Sweeps for the tile. The first word of its first row narrows the search
    /// to a handful of places before anything else is compared, which is what
    /// keeps a sweep of half a million words cheap enough to repeat.
    /// </summary>
    internal static bool TryFind(
        ReadOnlySpan<ushort> vram, int pitch, int height,
        ReadOnlySpan<ushort> tile, int words, int rows, out int wx, out int wy)
    {
        wx = wy = -1;
        if (words <= 0 || rows <= 0 || words > pitch || rows > height) return false;

        ushort first = tile[0];
        for (int y = 0; y + rows <= height; y++)
        {
            int at = y * pitch;
            for (int x = 0; x + words <= pitch; x++)
            {
                if (vram[at + x] != first) continue;
                if (!Matches(vram, pitch, height, tile, words, rows, x, y)) continue;
                wx = x;
                wy = y;
                return true;
            }
        }
        return false;
    }

    static void Paste(Span<ushort> vram, int pitch, ReadOnlySpan<ushort> tile,
                      int words, int rows, int wx, int wy)
    {
        for (int row = 0; row < rows; row++)
        {
            int at = (wy + row) * pitch + wx;
            for (int w = 0; w < words; w++)
                vram[at + w] = tile[row * words + w];
        }
    }

    /// <summary>
    /// Called every frame. Looks no more than twice a second, and does nothing
    /// at all once there is nothing left to find.
    /// </summary>
    public static void Tick()
    {
        if (!Wanted || !Load()) return;
        if (--_countdown > 0) return;
        _countdown = EveryFrames;

        if (Runtime.Gpu is not { } gpu) return;

        int pitch = Gpu.VramWidth, height = Gpu.VramHeight;
        _vram ??= new ushort[pitch * height];

        var backend = GpuHle.Backend is { Ready: true } b ? b : null;
        if (backend is not null) backend.ReadVram(0, 0, pitch, height, _vram);
        else Array.Copy(gpu.Vram, _vram, _vram.Length);

        var before = _before!.AsSpan();
        var after = _after!.AsSpan();

        // The place it was last found is checked first: a hit there is a few
        // hundred comparisons against a sweep of half a million.
        bool there = _foundX >= 0
            && Matches(_vram, pitch, height, before, _words, _rows, _foundX, _foundY);

        if (!there && !TryFind(_vram, pitch, height, before, _words, _rows,
                               out _foundX, out _foundY))
            return;

        Paste(_vram, pitch, after, _words, _rows, _foundX, _foundY);

        if (backend is not null)
        {
            // Only the tile goes back, not the megabyte it came in.
            var patch = new ushort[_words * _rows];
            for (int row = 0; row < _rows; row++)
                Array.Copy(_vram, (_foundY + row) * pitch + _foundX,
                           patch, row * _words, _words);
            backend.WriteVram(_foundX, _foundY, _words, _rows, patch);
        }
        else
        {
            Paste(gpu.Vram, pitch, after, _words, _rows, _foundX, _foundY);
        }

        _written++;
        Console.Error.WriteLine(
            $"[title] the menu's second item renamed at vram {_foundX},{_foundY}"
            + $" ({_written} time(s) so far)");
    }
}
