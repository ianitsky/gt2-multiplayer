#!/usr/bin/env python3
"""Walk the ISO9660 directory tree of a MODE2/2352 PlayStation disc image.

The recompiler only ever needs the two overlay containers, so nothing in the
project reads the disc's own file table. Track and car data live in files this
prints, which is why it exists.
"""
import struct
import sys

RAW = 2352
HDR = 24        # sync (12) + header (4) + subheader (8)
USER = 2048


def sector(f, lba):
    f.seek(lba * RAW + HDR)
    return f.read(USER)


def entries(data):
    off = 0
    while off < len(data):
        length = data[off]
        if length == 0:
            # Directory records never span a sector boundary; a zero length
            # means "rest of this sector is padding".
            off = (off // USER + 1) * USER
            if off >= len(data):
                return
            continue
        rec = data[off:off + length]
        extent = struct.unpack_from('<I', rec, 2)[0]
        size = struct.unpack_from('<I', rec, 10)[0]
        flags = rec[25]
        name_len = rec[32]
        name = rec[33:33 + name_len].decode('ascii', 'replace')
        if name not in ('\x00', '\x01'):
            yield name.split(';')[0], extent, size, bool(flags & 0x02)
        off += length


def read_dir(f, extent, size):
    data = b''.join(sector(f, extent + i) for i in range((size + USER - 1) // USER))
    return list(entries(data))


def walk(f, extent, size, prefix=''):
    for name, ext, sz, is_dir in read_dir(f, extent, size):
        path = f'{prefix}/{name}'
        if is_dir:
            print(f'{path}/')
            walk(f, ext, sz, path)
        else:
            print(f'{path}  lba={ext}  {sz} bytes')


def main(path):
    with open(path, 'rb') as f:
        pvd = sector(f, 16)
        root = pvd[156:190]
        extent = struct.unpack_from('<I', root, 2)[0]
        size = struct.unpack_from('<I', root, 10)[0]
        walk(f, extent, size)


if __name__ == '__main__':
    main(sys.argv[1])
