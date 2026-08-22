#!/usr/bin/env python3
"""Build a RecompOne function map from a gt2-reversing splat config.

The splat yaml carries the authoritative code/data boundaries: `code` segments
hold subsegments typed `c`/`asm` (text) alongside `rodata`/`data`/`bss` ones.
Feeding RecompOne a function that spills into rodata makes it disassemble data,
so functions are clipped to the text ranges taken from the yaml, and symbols
landing outside those ranges are dropped.
"""
import argparse
import bisect
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


def control_flow_targets(text, base, ranges):
    """Collect every address reached by a control-flow instruction.

    JAL is the obvious case, but GT2 also reaches shared code through plain
    jumps: at 0x8007C310 a run of thunks each load a different argument and
    `j` into a common body at 0x8007C32C. That body is not a symbol, so a
    dispatch to it has nowhere to land and the runtime raises "unmapped call".

    Branches are included for the same reason. Whether a target is a genuine
    entry point or merely an internal label is decided by the caller, which
    knows which function contains what.
    """
    targets = {}
    for lo, hi in ranges:
        for pc in range(lo, hi, 4):
            word = struct.unpack_from("<I", text, pc - base)[0]
            op = word >> 26
            is_call = op == 3  # JAL
            if op in (2, 3):  # J, JAL
                target = ((pc + 4) & 0xF0000000) | ((word & 0x03FFFFFF) << 2)
            elif op in (4, 5, 6, 7) or (op == 1 and ((word >> 16) & 0x1F) in (0, 1, 0x10, 0x11)):
                offset = word & 0xFFFF
                if offset & 0x8000:
                    offset -= 0x10000
                target = pc + 4 + offset * 4
            else:
                continue
            if any(lo2 <= target < hi2 for lo2, hi2 in ranges):
                # A call is an entry point wherever it lands, including inside
                # the function that issues it; a jump or branch to its own
                # function is just an internal label. Recording the source pc
                # only for the latter lets the caller tell them apart.
                targets.setdefault(target, None if is_call else pc)
    return targets


def computed_addresses(image, base, ranges):
    """Text addresses built in a register by a lui/addiu pair.

    A function pointer does not have to be a call site or a word in a table:
    GT2 also materialises one into a register and calls it indirectly, which
    is how 0x80068310 is reached. Only pairs resolving into the code ranges
    are kept, so ordinary constants are ignored.
    """
    found = set()
    limit = len(image) - 4
    for off in range(0, limit, 4):
        word = struct.unpack_from("<I", image, off)[0]
        if word >> 26 != 0x0F:  # LUI
            continue
        reg = (word >> 16) & 0x1F
        hi = word & 0xFFFF
        for step in range(4, 4 * 24, 4):
            if off + step + 4 > len(image):
                break
            nxt = struct.unpack_from("<I", image, off + step)[0]
            op = nxt >> 26
            if op == 0x09 and ((nxt >> 21) & 0x1F) == reg:  # ADDIU from that reg
                lo = nxt & 0xFFFF
                addr = ((hi << 16) + (lo - 0x10000 if lo & 0x8000 else lo)) & 0xFFFFFFFF
                if addr % 4 == 0 and any(l <= addr < h for l, h in ranges):
                    found.add(addr)
                break
            if op == 0x0F and ((nxt >> 16) & 0x1F) == reg:  # register reloaded
                break
    return found


def data_pointers(image, base, ranges):
    """Function pointers parked in the executable's data sections.

    GT2 registers BIOS event handlers through 12-byte descriptors holding
    {handler, class, spec}; the handler addresses appear nowhere in the code
    stream, only in this table. Only non-code regions are scanned, so
    instruction words are never mistaken for pointers.
    """
    pointers = set()
    for off in range(0, len(image) - 3, 4):
        addr = base + off
        if any(lo <= addr < hi for lo, hi in ranges):
            continue
        word = struct.unpack_from("<I", image, off)[0]
        if word % 4 == 0 and any(lo <= word < hi for lo, hi in ranges):
            pointers.add(word)
    return pointers


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


def build(syms, ranges, extra_entries=()):
    """Lay out functions, then add the discovered entry points on top.

    A named function runs to the next named function. A discovered entry point
    is a second way into a function that already exists, so it cannot be sized
    that way: the thunks at 0x8007C310 jump into a shared body, and a slice
    ending at the next entry point would stop before reaching it. Each entry
    point therefore runs to the END of its host, overlapping the host and any
    entry points between. The emitted slices duplicate the tail they share,
    which is what makes each one self-contained.
    """
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

    hosts = sorted((int(f["address"], 16), f["size"]) for f in functions)
    host_starts = [h[0] for h in hosts]
    for addr in sorted(extra_entries):
        i = bisect.bisect_right(host_starts, addr) - 1
        if i < 0:
            continue
        host_start, host_size = hosts[i]
        host_end = host_start + host_size
        if not host_start < addr < host_end:
            continue          # not interior to a host: already an entry, or in a gap
        functions.append({
            "address": f"0x{addr:08X}",
            "name": f"entry_{addr:08X}",
            "size": host_end - addr,
        })

    functions.sort(key=lambda f: int(f["address"], 16))
    return functions


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("yaml", help="splat config yaml")
    ap.add_argument("symbols", nargs="+", help="splat symbol_addrs files")
    ap.add_argument("-o", "--out", required=True)
    ap.add_argument("--disc", help="disc image; enables entry-point discovery")
    ap.add_argument("--image", help="raw code image instead of a disc, e.g. an overlay already "
                                    "extracted from GT2.OVL; enables the same discovery")
    ap.add_argument("--also-scan", action="append", default=[], metavar="IMAGE@BASE",
                    help="additional code image to scan for calls INTO this map's ranges. "
                         "Overlays call the main executable, and those call sites are "
                         "invisible when only the main image is scanned.")
    ap.add_argument("--text-base", default="0x80010000")
    ap.add_argument("--text-size", default="0x99000")
    args = ap.parse_args()

    cfg = yaml.safe_load(open(args.yaml, encoding="utf-8"))
    ranges = text_ranges(cfg)
    if not ranges:
        sys.exit(f"no code segments in {args.yaml}")

    syms = load_symbols(args.symbols)

    gap_entries = 0
    interior = set()
    if args.disc or args.image:
        base = int(args.text_base, 16)
        if args.image:
            # An overlay arrives already extracted and inflated, so it is the
            # code image directly rather than something to carve out of a disc.
            image = open(args.image, "rb").read()
        else:
            image = read_exe_text(args.disc, base, int(args.text_size, 16))

        candidates = dict(control_flow_targets(image, base, ranges))
        # An address also taken as a pointer is an entry point whatever else
        # reaches it. Recording it with no source overrides a branch that would
        # otherwise have it dismissed as an internal label: 0x80016258 and
        # 0x800218DC are each reached both ways, and letting the branch win kept
        # them out of the map entirely.
        for pointer in data_pointers(image, base, ranges):
            candidates[pointer] = None
        for computed in computed_addresses(image, base, ranges):
            candidates[computed] = None

        for spec in args.also_scan:
            path, _, other_base = spec.partition("@")
            other = open(path, "rb").read()
            ob = int(other_base or "0x80010000", 16)
            # Only targets landing in THIS map's ranges matter, and they are
            # always calls from outside, never internal labels - the source pc
            # belongs to a different image - so record them with no source.
            # Scan the whole of the other image, keeping only what lands here.
            own = [(ob, ob + (len(other) & ~3))]
            for addr in control_flow_targets(other, ob, own):
                if any(lo <= addr < hi for lo, hi in ranges):
                    candidates.setdefault(addr, None)
            for pointer in data_pointers(other, ob, []):
                if any(lo <= pointer < hi for lo, hi in ranges):
                    candidates[pointer] = None
            for computed in computed_addresses(other, ob, ranges):
                candidates[computed] = None

        named = sorted(syms)
        for addr, source_pc in sorted(candidates.items()):
            if addr in syms:
                continue
            i = bisect.bisect_right(named, addr) - 1
            owner = named[i] if i >= 0 else None
            if owner is None:
                # Before every symbol: a real function nobody named.
                syms[addr] = f"func_{addr:08X}"
                gap_entries += 1
            elif source_pc is not None and owner == (
                named[bisect.bisect_right(named, source_pc) - 1]
                if bisect.bisect_right(named, source_pc) > 0 else None
            ):
                continue      # branch within its own function: an internal label
            else:
                # Deliberately overlapping rather than splitting the host at
                # this address. Splitting looks tidier and gets coverage to
                # 100%, but the recompiler resolves jump tables against the
                # function it is emitting: a new boundary turns an in-function
                # case target into a dispatch to an address no symbol names,
                # and 0x8007C76C stops being reachable. Overlap duplicates the
                # shared tail; that is the cost of keeping those jumps local.
                interior.add(addr)

    functions = build(syms, ranges, interior)
    if not functions:
        sys.exit("no symbols fell inside the code ranges")

    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump({"functions": functions}, f, indent=2)
        f.write("\n")

    text_bytes = sum(e - s for s, e in ranges)
    covered = sum(f["size"] for f in functions)
    print(f"{args.out}: {len(functions)} functions in {len(ranges)} code range(s), "
          f"{covered}/{text_bytes} bytes ({covered/text_bytes:.1%})")
    if args.disc or args.image:
        print(f"  {len(interior)} interior entry point(s) recovered "
              f"(alternate ways into an existing function)")
        if gap_entries:
            print(f"  {gap_entries} unnamed function(s) found outside any symbol")
    for s, e in ranges:
        print(f"  text 0x{s:08X}-0x{e:08X}  ({e - s} bytes)")


if __name__ == "__main__":
    main()
