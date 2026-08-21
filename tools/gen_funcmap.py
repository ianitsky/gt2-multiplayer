#!/usr/bin/env python3
"""Build a RecompOne function map from a gt2-reversing splat config.

The splat yaml carries the authoritative code/data boundaries: `code` segments
hold subsegments typed `c`/`asm` (text) alongside `rodata`/`data`/`bss` ones.
Feeding RecompOne a function that spills into rodata makes it disassemble data,
so functions are clipped to the text ranges taken from the yaml, and symbols
landing outside those ranges are dropped.
"""
import argparse
import json
import re
import struct
import sys

import yaml

SYM_LINE = re.compile(r"^\s*([A-Za-z_][A-Za-z0-9_$]*)\s*=\s*0x([0-9A-Fa-f]{8})\s*;")
DATA_PREFIXES = ("D_", "str_", "jtbl_")
TEXT_TYPES = {"c", "asm", "hasm", ".text"}


def sub_entry(sub):
    """Return (offset, type) for a subsegment in list or dict form."""
    if isinstance(sub, list):
        return sub[0], (sub[1] if len(sub) > 1 else "bin")
    if isinstance(sub, dict):
        return sub.get("start"), sub.get("type", "bin")
    return sub, "bin"


def text_ranges(cfg):
    """Collect [start, end) vram ranges that hold code."""
    segments = cfg["segments"]
    ranges = []
    for i, seg in enumerate(segments):
        if not isinstance(seg, dict) or seg.get("type") != "code":
            continue
        base_off, base_vram = seg["start"], seg["vram"]
        subs = [sub_entry(s) for s in seg.get("subsegments", [])]
        # segment end: start of the next segment that declares one
        seg_end = None
        for nxt in segments[i + 1:]:
            if isinstance(nxt, dict) and "start" in nxt:
                seg_end = nxt["start"]
                break
        bounds = [off for off, _ in subs] + ([seg_end] if seg_end else [])
        for j, (off, kind) in enumerate(subs):
            if kind.lstrip(".") not in {t.lstrip(".") for t in TEXT_TYPES}:
                continue
            end = bounds[j + 1] if j + 1 < len(bounds) else seg_end
            if end is None:
                continue
            start_vram = base_vram + (off - base_off)
            ranges.append((start_vram, base_vram + (end - base_off)))

    ranges.sort()
    merged = []
    for start, end in ranges:
        if merged and start <= merged[-1][1]:
            merged[-1][1] = max(merged[-1][1], end)
        else:
            merged.append([start, end])
    return [tuple(r) for r in merged]


def load_symbols(paths):
    syms = {}
    for path in paths:
        for line in open(path, encoding="utf-8", errors="replace"):
            m = SYM_LINE.match(line)
            if not m:
                continue
            name, addr = m.group(1), int(m.group(2), 16)
            if name.startswith(DATA_PREFIXES):
                continue
            syms.setdefault(addr, name)
    return syms


def jal_targets(text, base, ranges):
    """Every address reached by a JAL is a function entry by definition.

    The splat symbol files name most functions but miss a handful of entry
    points; without them RecompOne raises "unmapped call" at runtime, since a
    call landing mid-function has nowhere to dispatch to.
    """
    targets = set()
    for lo, hi in ranges:
        for pc in range(lo, hi, 4):
            word = struct.unpack_from("<I", text, pc - base)[0]
            if word >> 26 == 3:  # JAL
                targets.add(((pc + 4) & 0xF0000000) | ((word & 0x03FFFFFF) << 2))
    return {a for a in targets if any(lo <= a < hi for lo, hi in ranges)}


def read_exe_text(disc, base, size, exe_sector=24, header=0x800, sector_size=2352):
    """Pull the boot executable's text out of a MODE2/2352 disc image."""
    with open(disc, "rb") as f:
        need = header + size
        data = b""
        sector = exe_sector
        while len(data) < need:
            f.seek(sector * sector_size + 24)
            chunk = f.read(2048)
            if not chunk:
                break
            data += chunk
            sector += 1
    return data[header:header + size]


def build(syms, ranges):
    functions = []
    for start, end in ranges:
        addrs = sorted(a for a in syms if start <= a < end)
        for i, addr in enumerate(addrs):
            nxt = addrs[i + 1] if i + 1 < len(addrs) else end
            functions.append({
                "address": f"0x{addr:08X}",
                "name": syms[addr],
                "size": nxt - addr,
            })
    functions.sort(key=lambda f: int(f["address"], 16))
    return functions


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("yaml", help="splat config yaml")
    ap.add_argument("symbols", nargs="+", help="splat symbol_addrs files")
    ap.add_argument("-o", "--out", required=True)
    ap.add_argument("--disc", help="disc image; enables JAL entry-point discovery")
    ap.add_argument("--text-base", default="0x80010000")
    ap.add_argument("--text-size", default="0x99000")
    args = ap.parse_args()

    cfg = yaml.safe_load(open(args.yaml, encoding="utf-8"))
    ranges = text_ranges(cfg)
    if not ranges:
        sys.exit(f"no code segments in {args.yaml}")

    syms = load_symbols(args.symbols)

    discovered = 0
    if args.disc:
        base = int(args.text_base, 16)
        text = read_exe_text(args.disc, base, int(args.text_size, 16))
        for addr in sorted(jal_targets(text, base, ranges)):
            if addr not in syms:
                syms[addr] = f"func_{addr:08X}"
                discovered += 1

    functions = build(syms, ranges)
    if not functions:
        sys.exit("no symbols fell inside the code ranges")

    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"functions": functions}, f, indent=2)
        f.write("\n")

    text_bytes = sum(e - s for s, e in ranges)
    covered = sum(f["size"] for f in functions)
    print(f"{args.out}: {len(functions)} functions in {len(ranges)} code range(s), "
          f"{covered}/{text_bytes} bytes ({covered/text_bytes:.1%})")
    if args.disc:
        print(f"  {discovered} unnamed entry point(s) recovered from JAL targets")
    for s, e in ranges:
        print(f"  text 0x{s:08X}-0x{e:08X}  ({e - s} bytes)")


if __name__ == "__main__":
    main()
