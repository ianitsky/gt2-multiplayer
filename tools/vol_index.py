#!/usr/bin/env python3
"""Name the files behind the indices the game opens.

GT2 resolves every path it knows once at boot and opens by index afterwards,
so a hook on the by-name lookup sees only the boot enumeration. The index is
the same value a GTFS entry carries, which makes it resolvable offline - this
turns a log full of bare numbers back into filenames.

Usage:
    python tools/vol_index.py <disc.cue-or-bin> [indices.txt]

With no file, reads indices on stdin. Any line containing a number works, so a
raw log can be piped straight in.
"""
import re
import sys

sys.path.insert(0, 'tools')
import gt2vol as G


def index_map(disc_path):
    disc = G.Disc(disc_path)
    vol = G.Vol(disc)

    names = {}

    def walk(entry_index, prefix):
        for _, name, value, flags in vol.listing(entry_index):
            if name == '..':
                continue
            path = f'{prefix}/{name}'
            if flags & G.DIRECTORY:
                walk(value, path)
            else:
                names[value] = path

    walk(0, '')
    return names


def main(argv):
    names = index_map(argv[1])
    source = open(argv[2]) if len(argv) > 2 else sys.stdin
    seen = set()
    for line in source:
        for token in re.findall(r'\d+', line):
            value = int(token)
            if value in seen:
                continue
            seen.add(value)
            print(f'{value}\t{names.get(value, "?")}')


if __name__ == '__main__':
    main(sys.argv)
