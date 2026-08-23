#!/usr/bin/env python3
"""Turn a PlayStation TIM into a PNG, so disc assets can be eyeballed.

The port decodes TIMs itself at runtime; this exists to check that what the
archive hands back is the picture we think it is, without launching the game.

Only the two paletted forms are handled - 4bpp and 8bpp - because that is what
the course maps use. 16bpp and 24bpp raise rather than guess.

Usage: python tools/tim2png.py <in.tim> <out.png>
"""
import struct
import sys
import zlib

MAGIC = 0x10
CLUT_PRESENT = 0x08


def decode(data):
    magic, flags = struct.unpack_from('<II', data, 0)
    if magic != MAGIC:
        raise SystemExit(f'not a TIM: magic {magic:#x}')
    depth = flags & 7  # pmode is bits 0-2; only 4bpp (0) and 8bpp (1) are handled
    if depth not in (0, 1):
        raise SystemExit(f'only 4bpp and 8bpp are handled, got depth code {depth}')

    off = 8
    palette = []
    if flags & CLUT_PRESENT:
        length, _, _, width, height = struct.unpack_from('<IHHHH', data, off)
        for i in range(width * height):
            c = struct.unpack_from('<H', data, off + 12 + i * 2)[0]
            # 5 bits per channel, BGR order, top bit is semi-transparency.
            r, g, b = (c & 31) << 3, (c >> 5 & 31) << 3, (c >> 10 & 31) << 3
            # Black with the flag clear is TIM's transparent colour.
            alpha = 0 if (c & 0x7FFF) == 0 and not (c & 0x8000) else 255
            palette.append((r, g, b, alpha))
        off += length

    length, _, _, words, rows = struct.unpack_from('<IHHHH', data, off)
    pixels = data[off + 12:off + length]
    width = words * (4 if depth == 0 else 2)

    out = []
    for y in range(rows):
        row = []
        base = y * words * 2
        for x in range(width):
            if depth == 0:
                byte = pixels[base + x // 2]
                index = byte & 15 if x % 2 == 0 else byte >> 4
            else:
                index = pixels[base + x]
            row.append(palette[index] if palette else (index, index, index, 255))
        out.append(row)
    return width, rows, out


def write_png(path, width, height, rows):
    raw = b''.join(b'\x00' + b''.join(bytes(p) for p in row) for row in rows)

    def chunk(tag, body):
        data = tag + body
        return struct.pack('>I', len(body)) + data + struct.pack('>I', zlib.crc32(data))

    png = (b'\x89PNG\r\n\x1a\n'
           + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0))
           + chunk(b'IDAT', zlib.compress(raw, 9))
           + chunk(b'IEND', b''))
    open(path, 'wb').write(png)


def main(argv):
    width, height, rows = decode(open(argv[1], 'rb').read())
    write_png(argv[2], width, height, rows)
    print(f'{width}x{height} -> {argv[2]}')


if __name__ == '__main__':
    main(sys.argv)
