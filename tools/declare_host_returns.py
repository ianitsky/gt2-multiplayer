#!/usr/bin/env python3
"""Declare every return address inside the function that owns an address.

longjmp unwinds to the boot loop, so the port carries on at RA - and a function
that yields through longjmp does it from many call sites, each of which becomes
a separate entry the recompiler has to know. Declaring them one failure at a
time costs a run per site; a function with 32 of them costs 32 runs. This does
the whole function at once.

Give it the address the run failed on and the overlay that was loaded, which
the log names. Declaring into an overlay that was not involved splits functions
on paths that currently work.

Usage:
    python tools/declare_host_returns.py <0xADDRESS> <image>
"""
import os
import re
import subprocess
import sys

GENERATED = 'generated'


def host_span(image, address):
    """The function containing the address, as (name, start line, end line)."""
    path = os.path.join(GENERATED, f'{image}.cs')
    lines = open(path, encoding='utf-8', errors='replace').read().split('\n')
    marker = f'/* 0x{address:08X} */'
    start = name = None
    for i, line in enumerate(lines):
        found = re.search(r'public static void ([A-Za-z0-9_]+)\(CpuContext', line)
        if found:
            if name is not None and start is not None and marker_seen:
                return name, start, i, lines
            name, start, marker_seen = found.group(1), i, False
        if marker in line and name is not None:
            marker_seen = True
    if name is not None and marker_seen:
        return name, start, len(lines), lines
    raise SystemExit(f'0x{address:08X} is not in {image}')


def main(argv):
    address, image = int(argv[1], 16), argv[2]
    name, start, end, lines = host_span(image, address)

    # Return addresses are only half of it. Cutting a function into slices
    # turns every branch that crosses a cut into a call, so the branch targets
    # need to be entries too - otherwise the first backward jump inside the
    # function fails exactly the way the return did.
    returns = []
    for line in lines[start:end]:
        found = re.search(r'c\.RA = 0x(8[0-9A-F]{7})u;', line)
        if found:
            returns.append(int(found.group(1), 16))
        for label in re.findall(r'L(8[0-9A-F]{7})', line):
            returns.append(int(label, 16))

    host = re.search(r'_(8[0-9A-F]{7})', name)
    wanted = []
    if host:
        wanted.append(f'0x{int(host.group(1), 16):08X}')
    # The address asked about is the one that failed; it has to be in the set
    # even when it is neither a return nor a label of this slice.
    returns.append(address)
    wanted += [f'0x{r:08X}' for r in sorted(set(returns))]
    print(f'{name} in {image}: {len(set(returns))} return and branch targets')
    subprocess.run([sys.executable, 'tools/declare_function.py', image] + wanted, check=True)


if __name__ == '__main__':
    main(sys.argv)
