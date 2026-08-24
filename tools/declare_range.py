#!/usr/bin/env python3
"""Declare every entry point a function needs, across its whole address range.

Cutting a function into slices turns each branch that crosses a cut into a
call, so the branch targets need entry points too. Scanning the current slice
is not enough: the slice shrinks with every cut, so the scan sees less each
time and the same function keeps failing one address further back. The
function's real extent comes from the funcmap, and every generated line
carries the address it came from, so the whole range can be swept at once.

An overlay has no funcmap with sizes, so its range has to be given directly.
The end is where the next untouched function begins - reading the generated
source around the address shows it.

Usage:
    python tools/declare_range.py <image> <function-name> [more names...]
    python tools/declare_range.py <image> --range <0xSTART> <0xEND>
"""
import json
import os
import re
import subprocess
import sys

GENERATED = 'generated'
CONFIG = 'config/gt2.json'


def span(image, name):
    path = os.path.join('config', 'funcmaps', f'{image}.json')
    data = json.load(open(path))
    for f in (data['functions'] if isinstance(data, dict) else data):
        if f['name'] == name:
            start = int(f['address'], 16)
            return start, start + f['size']
    raise SystemExit(f'{image} has no function named {name}')


def targets(image, start, end):
    """Return addresses and branch targets on every line inside the range."""
    found = set()
    path = os.path.join(GENERATED, f'{image}.cs')
    for line in open(path, encoding='utf-8', errors='replace'):
        at = re.search(r'/\* 0x(8[0-9A-F]{7}) \*/', line)
        if not at or not (start <= int(at.group(1), 16) < end):
            continue
        ra = re.search(r'c\.RA = 0x(8[0-9A-F]{7})u;', line)
        if ra:
            found.add(int(ra.group(1), 16))
        for label in re.findall(r'\bL(8[0-9A-F]{7})\b', line):
            found.add(int(label, 16))
    return {a for a in found if start <= a < end}


def main(argv):
    image, names = argv[1], argv[2:]
    wanted = []
    if names and names[0] == '--range':
        spans = [(int(names[1], 16), int(names[2], 16), f'0x{int(names[1], 16):08X}')]
    else:
        spans = [(*span(image, n), n) for n in names]
    for start, end, name in spans:
        inside = targets(image, start, end)
        print(f'{name}: 0x{start:08X}..0x{end:08X}, {len(inside)} targets')
        wanted.append(f'0x{start:08X}')
        wanted += [f'0x{a:08X}' for a in sorted(inside)]
    subprocess.run([sys.executable, 'tools/declare_function.py', image] + wanted, check=True)


if __name__ == '__main__':
    main(sys.argv)
