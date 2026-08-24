#!/usr/bin/env python3
"""Pull the six overlays out of a disc's GT2.OVL and point the config at them.

GT2.OVL opens with six (offset, length) pairs, each naming a gzip blob that
inflates to one overlay image. The recompiler reads them through the config's
offset and size, so changing disc means changing both together - which is what
this does, in one step, so the two cannot drift apart.

The byte-level patches in ovl_patched are carried across: they are read as a
diff against the current ovl_bin before anything is overwritten, and replayed
onto the new images. A patch whose bytes no longer match what it expects is
reported and skipped rather than applied blind.

Usage:
    python tools/extract_overlays.py <disc.bin>
"""
import json
import os
import struct
import sys
import zlib

RAW = 2352
SECTOR_HEADER = 24
USER = 2048
OVERLAY_COUNT = 6
BASE = 0x80010000
CONFIG = 'config/gt2.json'
RAW_DIR = 'ovl_bin'
PATCHED_DIR = 'ovl_patched'


def read_file(path, lba, size):
    with open(path, 'rb') as f:
        out = bytearray()
        for i in range((size + USER - 1) // USER):
            f.seek((lba + i) * RAW + SECTOR_HEADER)
            out += f.read(USER)
    return bytes(out[:size])


def find_gt2_ovl(path):
    """The disc's own directory says where GT2.OVL is; do not assume."""
    with open(path, 'rb') as f:
        f.seek(16 * RAW + SECTOR_HEADER)
        pvd = f.read(USER)
    extent = struct.unpack_from('<I', pvd, 158)[0]
    size = struct.unpack_from('<I', pvd, 166)[0]
    data = read_file(path, extent, size)
    off = 0
    while off < len(data) and data[off]:
        rec = data[off:off + data[off]]
        name = rec[33:33 + rec[32]].decode('ascii', 'replace').split(';')[0]
        if name == 'GT2.OVL':
            return struct.unpack_from('<I', rec, 2)[0], struct.unpack_from('<I', rec, 10)[0]
        off += data[off]
    raise SystemExit(f'{path}: no GT2.OVL in the root directory')


def existing_patches():
    """Every byte ovl_patched changes, as (overlay, offset, before, after)."""
    patches = []
    for i in range(1, OVERLAY_COUNT + 1):
        name = f'gt2_{i:02}'
        raw = os.path.join(RAW_DIR, f'{name}.bin')
        patched = os.path.join(PATCHED_DIR, f'{name}.bin')
        if not (os.path.exists(raw) and os.path.exists(patched)):
            continue
        a = open(raw, 'rb').read()
        b = open(patched, 'rb').read()
        for k in range(min(len(a), len(b))):
            if a[k] != b[k]:
                patches.append((name, k, a[k], b[k]))
    return patches


def main(argv):
    disc = argv[1]
    patches = existing_patches()
    print(f'{len(patches)} patched bytes to carry across')

    lba, size = find_gt2_ovl(disc)
    container = read_file(disc, lba, size)

    os.makedirs(RAW_DIR, exist_ok=True)
    os.makedirs(PATCHED_DIR, exist_ok=True)

    table = []
    for i in range(OVERLAY_COUNT):
        offset, length = struct.unpack_from('<2I', container, i * 8)
        image = zlib.decompress(container[offset:offset + length], 16 + zlib.MAX_WBITS)
        name = f'gt2_{i + 1:02}'
        open(os.path.join(RAW_DIR, f'{name}.bin'), 'wb').write(image)
        table.append((name, offset, length, len(image)))
        print(f'  {name}: packed {length} at {offset} -> {len(image)} bytes')

    applied = skipped = 0
    images = {name: bytearray(open(os.path.join(RAW_DIR, f'{name}.bin'), 'rb').read())
              for name, _, _, _ in table}
    for name, at, before, after in patches:
        image = images.get(name)
        if image is None or at >= len(image):
            print(f'  skipped {name} 0x{BASE + at:08X}: past the end of the new image')
            skipped += 1
            continue
        if image[at] != before:
            print(f'  skipped {name} 0x{BASE + at:08X}: expected {before:02X}, found {image[at]:02X}')
            skipped += 1
            continue
        image[at] = after
        applied += 1
    for name, image in images.items():
        open(os.path.join(PATCHED_DIR, f'{name}.bin'), 'wb').write(bytes(image))
    print(f'{applied} patched bytes applied, {skipped} skipped')

    config = json.load(open(CONFIG))
    by_name = {o['name']: o for o in config['overlays']}
    for name, offset, length, _ in table:
        if name in by_name:
            by_name[name]['offset'] = offset
            by_name[name]['size'] = length
    cue = os.path.splitext(disc)[0] + '.cue'
    config['cue'] = '../' + cue.replace('\\', '/')
    json.dump(config, open(CONFIG, 'w'), indent=2)
    print(f"config points at {config['cue']}")


if __name__ == '__main__':
    main(sys.argv)
