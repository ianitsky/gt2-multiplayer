#!/usr/bin/env python3
"""Read GT2.VOL, the GTFS archive holding every asset the game loads.

Nothing in the port needed this before: overlays come from GT2.OVL, and the
game reaches everything else through its own GTFS code. Track and car tables
live in here, so listing them means reading the archive ourselves.

Layout, worked out from the image rather than from documentation:

    0x00  'GTFS'
    0x04  0
    0x08  build timestamp
    0x0C  0
    0x10  count of something the reader does not need
    0x14  u32[] byte offsets into the VOL, monotonically increasing. The
          table stores each file's END, so a file whose entry carries value v
          runs offsets[v - 1] .. offsets[v]. Every file also begins on its own
          2KB sector, and offsets[v - 1] lands inside that first sector rather
          than exactly on it - round down. Verified by decompressing every
          gzip in the archive from the rounded start: 5114 of 5114, sizes
          from 652 bytes to 336 KB. The run ends where it stops increasing,
          which is also where it is padded to the entry table.
    0xB800 (padded) 32-byte entries:
            +0  u32 timestamp
            +4  u16 value
            +6  u8  flags: 0x01 directory, 0x80 last entry of its directory
            +7  name, NUL padded

    For a directory, value is the entry index its listing starts at; the
    listing runs until an entry carrying 0x80. For a file, value indexes the
    offset table. Every directory's listing opens with '..' pointing back at
    its parent's listing.

Usage:
    python tools/gt2vol.py <disc.bin> ls [path]
    python tools/gt2vol.py <disc.bin> cat <path> [out]
"""
import struct
import sys
import zlib

RAW = 2352
SECTOR_HEADER = 24
USER = 2048

VOL_NAME = 'GT2.VOL'
ENTRY_SIZE = 32
DIRECTORY = 0x01
LAST = 0x80


class Disc:
    """Just enough ISO9660 to find GT2.VOL and read bytes out of it."""

    def __init__(self, path):
        self._f = open(path, 'rb')
        self.vol_lba, self.vol_size = self._find(VOL_NAME)

    def _sector(self, lba):
        self._f.seek(lba * RAW + SECTOR_HEADER)
        return self._f.read(USER)

    def _find(self, name):
        pvd = self._sector(16)
        extent, size = struct.unpack_from('<I', pvd, 158)[0], struct.unpack_from('<I', pvd, 166)[0]
        data = b''.join(self._sector(extent + i) for i in range((size + USER - 1) // USER))
        off = 0
        while off < len(data):
            length = data[off]
            if length == 0:
                off = (off // USER + 1) * USER
                if off >= len(data):
                    break
                continue
            rec = data[off:off + length]
            entry = rec[33:33 + rec[32]].decode('ascii', 'replace').split(';')[0]
            if entry == name:
                return struct.unpack_from('<I', rec, 2)[0], struct.unpack_from('<I', rec, 10)[0]
            off += length
        raise SystemExit(f'{name} not found on the disc')

    def read(self, offset, length):
        """Bytes at a byte offset inside GT2.VOL, across sector boundaries."""
        out = bytearray()
        while length > 0:
            lba = self.vol_lba + offset // USER
            start = offset % USER
            chunk = self._sector(lba)[start:start + min(length, USER - start)]
            if not chunk:
                break
            out += chunk
            offset += len(chunk)
            length -= len(chunk)
        return bytes(out)


class Vol:
    def __init__(self, disc):
        self._disc = disc
        head = disc.read(0, 0x10)
        if head[:4] != b'GTFS':
            raise SystemExit('GT2.VOL does not start with GTFS')

        # The offset table runs until it stops increasing. Read it in blocks
        # rather than guessing a count: the header's count field is not it.
        offsets = []
        pos = 0x14
        done = False
        while not done:
            block = disc.read(pos, USER * 8)
            for value in struct.unpack_from('<%dI' % (len(block) // 4), block, 0):
                if offsets and value <= offsets[-1]:
                    done = True
                    break
                offsets.append(value)
            pos += len(block)
        self.offsets = offsets

        # Entries begin at the next 2KB boundary at or after the table's end.
        table_end = 0x14 + len(offsets) * 4
        self.entry_base = (table_end + USER - 1) // USER * USER
        self._entries = {}

    def entry(self, index):
        if index not in self._entries:
            raw = self._disc.read(self.entry_base + index * ENTRY_SIZE, ENTRY_SIZE)
            value = struct.unpack_from('<H', raw, 4)[0]
            flags = raw[6]
            name = raw[7:].split(b'\x00')[0].decode('ascii', 'replace')
            self._entries[index] = (name, value, flags)
        return self._entries[index]

    def listing(self, first):
        """Every entry of one directory, '..' included, in stored order."""
        out = []
        index = first
        while True:
            name, value, flags = self.entry(index)
            out.append((index, name, value, flags))
            if flags & LAST:
                return out
            index += 1

    def resolve(self, path):
        """Entry index for a slash-separated path. Empty path is the root."""
        index = 0
        for part in [p for p in path.split('/') if p]:
            for _, name, value, flags in self.listing(index):
                if name == part:
                    index = value if flags & DIRECTORY else _FileRef(value)
                    break
            else:
                raise SystemExit(f'no such entry: {path}')
            if isinstance(index, _FileRef):
                return index
        return index

    def read_file(self, ref):
        start, end = self.offsets[ref.index - 1], self.offsets[ref.index]
        data = self._disc.read(start // USER * USER, end - start)
        if data[:2] == b'\x1f\x8b':
            return zlib.decompress(data, 16 + zlib.MAX_WBITS)
        return data


class _FileRef:
    def __init__(self, index):
        self.index = index


def main(argv):
    disc = Disc(argv[1])
    vol = Vol(disc)
    command = argv[2] if len(argv) > 2 else 'ls'

    if command == 'ls':
        target = vol.resolve(argv[3] if len(argv) > 3 else '')
        if isinstance(target, _FileRef):
            raise SystemExit('that is a file, not a directory')
        for _, name, value, flags in vol.listing(target):
            if name == '..':
                continue
            if flags & DIRECTORY:
                print(f'{name}/')
            else:
                size = vol.offsets[value] - vol.offsets[value - 1]
                print(f'{name}\t{size}')
        return

    if command == 'cat':
        ref = vol.resolve(argv[3])
        if not isinstance(ref, _FileRef):
            raise SystemExit('that is a directory, not a file')
        data = vol.read_file(ref)
        if len(argv) > 4:
            open(argv[4], 'wb').write(data)
            print(f'{len(data)} bytes -> {argv[4]}')
        else:
            sys.stdout.buffer.write(data)
        return

    raise SystemExit(__doc__)


if __name__ == '__main__':
    main(sys.argv)
