namespace GT2Port.Multiplayer;

/// <summary>
/// Decodes a PlayStation TIM - the paletted line-art format the disc's course
/// map pictures ship in under crsmap/&lt;code&gt;.tim.gz, once <see cref="VolArchive"/>
/// has ungzipped one. Ported from tools/tim2png.py, the reference decoder whose
/// comments record the colour rules (BGR555, and all-zero-with-the-high-bit-clear
/// as transparent black). Only 4bpp and 8bpp are handled, since no course map uses
/// anything else; 16bpp and 24bpp report failure rather than guessing. Never
/// throws, in the same spirit as RoomState.TryDeserialise: this parses bytes an
/// archive handed back, so it bounds-checks before every read rather than relying
/// on an exception to catch a malformed or truncated file - including a palette
/// index that runs past the end of the CLUT, which is malformed input, not a
/// crash.
/// </summary>
public static class Tim
{
    const uint Magic = 0x10;
    const uint ClutPresentFlag = 0x08;

    // Comfortably larger than any real TIM (a course map is 96x96x4 bytes; a
    // full VRAM texture page tops out far below this) - just enough to stop a
    // malformed words/rows pair from asking for an absurd allocation.
    const long MaxOutputBytes = 64L * 1024 * 1024;

    public static bool TryDecode(byte[] data, out int width, out int height, out byte[] rgba)
    {
        width = 0;
        height = 0;
        rgba = [];

        ReadOnlySpan<byte> span = data;
        int offset = 0;

        if (!TryU32(span, ref offset, out uint magic) || magic != Magic) return false;
        if (!TryU32(span, ref offset, out uint flags)) return false;

        uint depth = flags & 3;
        if (depth != 0 && depth != 1) return false; // only 4bpp and 8bpp are handled

        ushort[]? palette = null;
        if ((flags & ClutPresentFlag) != 0 && !TryClut(span, ref offset, out palette)) return false;

        int blockStart = offset;
        if (!TryU32(span, ref offset, out uint length)) return false;
        if (length < 12 || (long)blockStart + length > span.Length) return false;
        if (!TryU16(span, ref offset, out _)) return false; // vram x
        if (!TryU16(span, ref offset, out _)) return false; // vram y
        if (!TryU16(span, ref offset, out ushort words)) return false;
        if (!TryU16(span, ref offset, out ushort rows)) return false;

        long neededPixelBytes = (long)words * 2 * rows;
        if (neededPixelBytes > length - 12) return false;

        int w = words * (depth == 0 ? 4 : 2);
        long outputBytes = (long)w * rows * 4;
        if (outputBytes > MaxOutputBytes) return false;

        var outPixels = new byte[outputBytes];
        for (int y = 0; y < rows; y++)
        {
            int rowStart = offset + y * words * 2;
            for (int x = 0; x < w; x++)
            {
                int index = depth == 0
                    ? ReadNibble(span, rowStart, x)
                    : span[rowStart + x];

                if (!TryColour(palette, index, out byte r, out byte g, out byte b, out byte a)) return false;

                int p = (y * w + x) * 4;
                outPixels[p] = r;
                outPixels[p + 1] = g;
                outPixels[p + 2] = b;
                outPixels[p + 3] = a;
            }
        }

        width = w;
        height = rows;
        rgba = outPixels;
        return true;
    }

    static int ReadNibble(ReadOnlySpan<byte> data, int rowStart, int x)
    {
        byte b = data[rowStart + x / 2];
        return (x % 2 == 0) ? (b & 0xF) : (b >> 4); // low nibble first
    }

    /// <summary>
    /// Looks up a palette index, or - with no CLUT - uses the index itself as a
    /// grey level, matching tools/tim2png.py. False when the index runs past the
    /// end of a present palette: malformed input, not a crash.
    /// </summary>
    static bool TryColour(ushort[]? palette, int index, out byte r, out byte g, out byte b, out byte a)
    {
        if (palette is null)
        {
            r = g = b = (byte)index;
            a = 255;
            return true;
        }

        if (index < 0 || index >= palette.Length)
        {
            r = g = b = a = 0;
            return false;
        }

        ushort c = palette[index];
        r = (byte)((c & 31) << 3);
        g = (byte)(((c >> 5) & 31) << 3);
        b = (byte)(((c >> 10) & 31) << 3);
        // 15 low bits zero and the semi-transparency bit clear is TIM's
        // transparent black; everything else is opaque.
        a = (c & 0x7FFF) == 0 ? (byte)0 : (byte)255;
        return true;
    }

    /// <summary>Reads the CLUT block: its length-prefixed header, then that many colours.</summary>
    static bool TryClut(ReadOnlySpan<byte> data, ref int offset, out ushort[]? palette)
    {
        palette = null;
        int blockStart = offset;

        if (!TryU32(data, ref offset, out uint length)) return false;
        if (length < 12 || (long)blockStart + length > data.Length) return false;

        if (!TryU16(data, ref offset, out _)) return false; // vram x
        if (!TryU16(data, ref offset, out _)) return false; // vram y
        if (!TryU16(data, ref offset, out ushort entriesWide)) return false;
        if (!TryU16(data, ref offset, out ushort entriesHigh)) return false;

        long count = (long)entriesWide * entriesHigh;
        long neededBytes = count * 2;
        if (neededBytes > length - 12) return false;

        var pal = new ushort[count];
        for (int i = 0; i < pal.Length; i++)
            pal[i] = BitConverter.ToUInt16(data.Slice(offset + i * 2, 2));

        palette = pal;
        offset = blockStart + (int)length;
        return true;
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
