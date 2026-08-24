#!/usr/bin/env python3
"""Declare a return address the unwind lands on, host included.

longjmp is modelled as an unwind to the boot loop, so a resume point returning
leaves the port to carry on at RA. That address is usually interior to a
function, and declaring an interior address alone splits its host - which turns
a working function into an unmapped call. This finds the host in the generated
source and declares both, which keeps them side by side.

Pass the overlay that was loaded when the call failed - the log names it. The
same address exists in several overlays with different code, and declaring it
everywhere splits functions in overlays that had nothing to do with the
failure, which trades a working path for a broken one.

Usage:
    python tools/declare_return.py <0xADDRESS> [image]
"""
import json
import os
import re
import subprocess
import sys

GENERATED = 'generated'
CONFIG = 'config/gt2.json'


def owners(address):
    """Every image holding this address, with the function containing it.

    An address inside the overlay window exists in more than one image, each
    with different code, and which one is live depends on what was loaded. So
    it is declared in all of them - which also makes the recompiler dispatch
    calls to it dynamically instead of binding one image's version.
    """
    marker = f'/* 0x{address:08X} */'
    found_in = []
    for name in sorted(os.listdir(GENERATED)):
        if not name.endswith('.cs'):
            continue
        image = os.path.splitext(name)[0]
        current = None
        with open(os.path.join(GENERATED, name), encoding='utf-8', errors='replace') as f:
            for line in f:
                hit = re.search(r'public static void ([A-Za-z0-9_]+)\(CpuContext', line)
                if hit:
                    current = hit.group(1)
                if marker in line:
                    found_in.append((image, current))
                    break
    return found_in


def address_of(image, function):
    """A generated name of the form entry_XXXXXXXX or func_XXXXXXXX carries its address."""
    found = re.search(r'_(8[0-9A-F]{7})', function or '')
    if found:
        return int(found.group(1), 16)
    path = os.path.join('config', 'funcmaps', f'{image}.json')
    if os.path.exists(path):
        data = json.load(open(path))
        for f in (data['functions'] if isinstance(data, dict) else data):
            if f['name'] == function:
                return int(f['address'], 16)
    return None


def main(argv):
    address = int(argv[1], 16)
    wanted_image = argv[2] if len(argv) > 2 else None
    found = owners(address)
    if wanted_image:
        found = [f for f in found if f[0] == wanted_image]
        if not found:
            raise SystemExit(f'0x{address:08X} is not in {wanted_image}')
    if not found:
        raise SystemExit(f'0x{address:08X} is not in any generated image')

    for image, function in found:
        host = address_of(image, function)
        print(f'0x{address:08X} is inside {function} ({image})')
        wanted = [f'0x{address:08X}']
        if host is not None and host != address:
            wanted.insert(0, f'0x{host:08X}')
            print(f'  declaring its host 0x{host:08X} alongside, so the split does not lose it')
        subprocess.run([sys.executable, 'tools/declare_function.py', image] + wanted, check=True)


if __name__ == '__main__':
    main(sys.argv)
