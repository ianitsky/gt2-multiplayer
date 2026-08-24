#!/usr/bin/env python3
"""Declare a function the linear sweep did not find.

Overlays are swept linearly, so a target reached only through a jump table is
invisible and shows up at runtime as "unmapped call". Adding it here is how the
port has always closed those: see the commits declaring the Options path and
the entry the demo returns through.

Declaring the same address in a second overlay does something else, and it is
often what you actually want: the recompiler stops binding calls to it
statically and emits Dispatcher.Call instead. Overlays load over the main
executable and over each other, so an address inside the overlay window can
belong to a different image by the time it is jumped to - a static binding then
runs whichever image happened to be recompiled, which is how the arcade branch
came to run stale code and fall out of the game loop entirely.

Usage:
    python tools/declare_function.py <overlay> <0xADDRESS> [more addresses...]
"""
import json
import os
import sys

CONFIG = 'config/gt2.json'


def main(argv):
    overlay, addresses = argv[1], argv[2:]
    config = json.load(open(CONFIG))
    if overlay == 'main':
        # The main executable's functions live at the top level, not under an
        # overlay - the unwind chain crosses into it once it climbs past the
        # overlay window.
        entry = config
    else:
        entry = next((o for o in config['overlays'] if o['name'] == overlay), None)
        if entry is None:
            raise SystemExit(f'no overlay named {overlay}')

    functions = entry.setdefault('functions', [])
    known = {f['address'].lower() for f in functions}

    # An address the funcmap already names must not be re-declared here: this
    # list wins, so declaring it again replaces a meaningful symbol with
    # entry_XXXXXXXX and every call site loses the name.
    funcmap = config.get('funcMap') if overlay == 'main' else None
    if funcmap:
        path = os.path.join(os.path.dirname(CONFIG), funcmap)
        if os.path.exists(path):
            data = json.load(open(path))
            for f in (data['functions'] if isinstance(data, dict) else data):
                known.add(f['address'].lower())
    for address in addresses:
        address = address.lower()
        if address in known:
            print(f'{address} already declared')
            continue
        functions.append({'address': address.upper().replace('0X', '0x'),
                          'name': f'entry_{address[2:].upper()}'})
        print(f'{overlay}: declared {address}')
    functions.sort(key=lambda f: int(f['address'], 16))
    json.dump(config, open(CONFIG, 'w'), indent=2)


if __name__ == '__main__':
    main(sys.argv)
