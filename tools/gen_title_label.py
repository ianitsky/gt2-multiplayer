#!/usr/bin/env python3
"""Build the patch that renames the title menu's second item.

The label is pixels, not text: arcade/title_item.tim.gz is a 4bpp sheet of
every title-menu label in every language, and the second item's tile sits at
x 108..211, y 166..187 of it. The sheet carries no palette of its own - its
CLUTs are packed into rows 240..247 of the same picture, and the one this tile
is drawn with is at row 242, word 16.

What comes out is a pair of pictures of the same tile: the one the disc has,
and the one to put in its place. The port finds the first in video memory and
writes the second over it, so nobody's disc is touched - see TitleLabel.

The replacement is drawn rather than assembled out of the sheet's own glyphs.
Three of the letters "Multiplayer" needs - p, y and r - are not in "Simulation
Mode", and the labels that do have them are drawn with a different CLUT, so
borrowing them means borrowing the wrong colours. Arial Bold Italic at 15px
condensed to the tile's width is the same shape to within a pixel, and every
pixel it lands on is quantised into this tile's own palette.

Usage:
    python tools/gen_title_label.py <disc.bin> [config/title-multiplayer.bin]
"""
import struct
import sys
import zlib

sys.path.insert(0, 'tools')
import gt2vol as G

try:
    from PIL import Image, ImageDraw, ImageFont, ImageFilter
except ImportError:
    sys.exit("this needs Pillow: pip install pillow")

SHEET = 'arcade/title_item.tim.gz'

# The tile, in sheet pixels. x is a multiple of four so the tile is whole
# words in a 4bpp picture, which is what video memory holds.
TX0, TY0, TX1, TY1 = 108, 166, 212, 188

# Where the text starts, leaving the blue mark that precedes every label.
TEXT_X = 13

CLUT_ROW, CLUT_WORD = 242, 16

# Palette indices this tile's CLUT gives white, two greys and the dark edge,
# and the one it leaves transparent.
CORE, MID, SOFT, EDGE, CLEAR = 10, 12, 14, 1, 11

FONT = r"C:\Windows\Fonts\arialbi.ttf"

# What the item should read, and the body that matches its neighbours.
#
# "Multiplayer" rather than "Multiplayer Mode": at this body the longer one
# runs past the tile and has to be condensed to fit, which reads visibly
# narrower than "Arcade Mode" above it. The menu already carries an item with
# no "Mode" in it - "Replay Theater" - so the short one is not the odd one out.
TEXT = 'Multiplayer'
SIZE = 16
BASELINE = 2
SUPERSAMPLE = 4

MAGIC = b'G2TL'
VERSION = 1


def sheet_pixels(disc_path):
    disc = G.Disc(disc_path)
    vol = G.Vol(disc)
    data = vol.read_file(vol.resolve(SHEET))
    if data[:2] == b'\x1f\x8b':
        data = zlib.decompressobj(31).decompress(data)

    magic, flags = struct.unpack_from('<II', data, 0)
    if magic != 0x10:
        sys.exit('%s is not a TIM' % SHEET)
    if flags & 7 != 0:
        sys.exit('%s is not 4bpp' % SHEET)
    if flags & 8:
        sys.exit('%s carries a CLUT block, which this does not expect' % SHEET)

    _, _, _, words, rows = struct.unpack_from('<IHHHH', data, 8)
    return data, 8 + 12, words, rows


def main(argv):
    if len(argv) < 2:
        sys.exit(__doc__)
    disc_path = argv[1]
    out_path = argv[2] if len(argv) > 2 else 'config/title-multiplayer.bin'

    data, base, words, rows = sheet_pixels(disc_path)

    def index(px, py):
        byte = data[base + py * words * 2 + px // 2]
        return byte & 15 if px % 2 == 0 else byte >> 4

    def word(px, py):
        # Four pixels to a 16-bit word, low nibble first.
        o = base + py * words * 2 + px // 2
        return data[o] | (data[o + 1] << 8)

    tw, th = TX1 - TX0, TY1 - TY0
    before = [[index(TX0 + x, TY0 + y) for x in range(tw)] for y in range(th)]

    # The new tile: the mark kept, the text cleared, the words drawn over it.
    after = [row[:] for row in before]
    for y in range(th):
        for x in range(TEXT_X, tw):
            after[y][x] = CLEAR

    room = tw - TEXT_X - 1
    font = ImageFont.truetype(FONT, SIZE * SUPERSAMPLE)
    big = Image.new('L', (tw * SUPERSAMPLE * 3, th * SUPERSAMPLE), 0)
    ImageDraw.Draw(big).text((0, BASELINE * SUPERSAMPLE), TEXT, font=font, fill=255)
    drawn = big.getbbox()[2]
    big = big.crop((0, 0, drawn, th * SUPERSAMPLE))
    if drawn / SUPERSAMPLE > room:
        big = big.resize((room * SUPERSAMPLE, th * SUPERSAMPLE), Image.LANCZOS)

    canvas = Image.new('L', (tw * SUPERSAMPLE, th * SUPERSAMPLE), 0)
    canvas.paste(big, (TEXT_X * SUPERSAMPLE, 0))
    mask = canvas.resize((tw, th), Image.LANCZOS)
    ink = mask.load()
    halo = mask.filter(ImageFilter.MaxFilter(3)).load()

    for y in range(th):
        for x in range(TEXT_X, tw):
            a = ink[x, y]
            if a > 170:
                after[y][x] = CORE
            elif a > 110:
                after[y][x] = MID
            elif a > 55:
                after[y][x] = SOFT
            elif halo[x, y] > 90:
                after[y][x] = EDGE

    def pack(tile):
        out = bytearray()
        for y in range(th):
            for wx in range(0, tw, 4):
                w = (tile[y][wx] | (tile[y][wx + 1] << 4)
                     | (tile[y][wx + 2] << 8) | (tile[y][wx + 3] << 12))
                out += struct.pack('<H', w)
        return bytes(out)

    # Read the "before" words straight out of the file rather than repacking
    # the nibbles, so a mistake in the unpacking cannot make a needle that
    # never matches what is actually in memory.
    raw_before = bytearray()
    for y in range(TY0, TY1):
        o = base + y * words * 2 + TX0 // 2
        raw_before += data[o:o + tw // 2]

    if bytes(raw_before) != pack(before):
        sys.exit('the tile did not survive a round trip - the geometry is wrong')

    blob = (MAGIC + struct.pack('<HHH', VERSION, tw // 4, th)
            + bytes(raw_before) + pack(after))
    with open(out_path, 'wb') as f:
        f.write(blob)

    # A picture of what was written, for looking at without running the game.
    clut_at = base + CLUT_ROW * words * 2 + CLUT_WORD * 2
    palette = [struct.unpack_from('<H', data, clut_at + i * 2)[0] for i in range(16)]

    def colour(c):
        return ((c & 31) * 8, ((c >> 5) & 31) * 8, ((c >> 10) & 31) * 8)

    preview = Image.new('RGB', (tw, th * 2 + 2), (0, 0, 0))
    for y in range(th):
        for x in range(tw):
            preview.putpixel((x, y), colour(palette[before[y][x]]))
            preview.putpixel((x, y + th + 2), colour(palette[after[y][x]]))
    preview.resize((tw * 6, (th * 2 + 2) * 6), Image.NEAREST).save(
        out_path + '.png')

    print('%s: %d words x %d rows, %d bytes' % (out_path, tw // 4, th, len(blob)))
    print('%s.png: the tile before and after' % out_path)


if __name__ == '__main__':
    main(sys.argv)
