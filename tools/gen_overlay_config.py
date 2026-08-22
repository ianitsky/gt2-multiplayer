#!/usr/bin/env python3
"""Regenerate the overlays section of the recompiler config.

Overlay offsets come from GT2.OVL's own table so they are never transcribed by
hand. How each overlay gets its functions depends on what gt2-reversing knows
about it:

  * gt2_05 and gt2_06 have real symbol coverage, so they use a symbol-driven
    function map and keep their original names.
  * the rest declare almost no text in their splat yaml - gt2_02 claims 120
    bytes for a 248KB overlay - so a symbol-driven map there is useless. They
    let the linear sweep find function boundaries and declare the addresses
    discovery found, which is what makes indirectly-reached code dispatchable.

Sizes are deliberately not declared for the swept overlays: a declared size
would override the sweep's boundaries with one derived from sparse symbols.
"""
import argparse
import gzip
import json
import pathlib
import struct
import subprocess
import sys

sys.path.insert(0, "tools")
from gen_funcmap import computed_addresses, data_pointers  # noqa: E402

SECTOR = 2352
SYMBOL_DRIVEN = {"gt2_05", "gt2_06"}


def read_ovl(disc, lba=331, size=289752):
    data = b""
    sector = lba
    with open(disc, "rb") as f:
        while len(data) < size:
            f.seek(sector * SECTOR + 24)
            data += f.read(2048)
            sector += 1
    return data[:size]


def entries(ovl):
    out, off = [], 0
    while True:
        offset, length = struct.unpack_from("<II", ovl, off)
        out.append((offset, length))
        off += 8
        if off >= out[0][0]:
            break
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("disc")
    ap.add_argument("--config", default="config/gt2.json")
    ap.add_argument("--config-dir", default="externals/gt2-reversing/config/gt2_us12_simdisk")
    ap.add_argument("--bin-dir", default="ovl_bin")
    args = ap.parse_args()

    ovl = read_ovl(args.disc)
    pathlib.Path(args.bin_dir).mkdir(exist_ok=True)

    overlays = []
    for i, (offset, length) in enumerate(entries(ovl), 1):
        name = f"gt2_0{i}"
        blob = ovl[offset:offset + length]
        if blob[:2] != b"\x1f\x8b":
            sys.exit(f"{name}: no gzip magic at offset {offset}")
        image = pathlib.Path(args.bin_dir) / f"{name}.bin"
        image.write_bytes(gzip.decompress(blob))

        entry = {
            "name": name, "file": "GT2.OVL",
            "offset": offset, "size": length, "gzip": True,
            "base": "0x80010000",
        }

        funcmap = f"config/funcmaps/{name}.json"
        cmd = [sys.executable, "tools/gen_funcmap.py",
               f"{args.config_dir}/{name}.yaml",
               f"{args.config_dir}/ovr{i}_symbol_addrs.txt",
               "--image", str(image), "-o", funcmap]
        if name in SYMBOL_DRIVEN:
            subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL)
            entry["funcMap"] = f"funcmaps/{name}.json"
            entry["linearSweep"] = False
        else:
            # Declare only addresses the code takes as a POINTER - built into a
            # register by lui/addiu, or stored in a table. Those are genuine
            # entry points, reached indirectly and invisible to the sweep.
            #
            # Branch and jump targets are deliberately excluded even though
            # discovery finds them: the recompiler splits a function at every
            # declared address, so declaring an internal label truncates the
            # function around it. Declaring them moved the crash earlier, into
            # code that had been working.
            raw = image.read_bytes()
            base = 0x80010000
            own = [(base, base + (len(raw) & ~3))]
            pointers = sorted(computed_addresses(raw, base, own) | data_pointers(raw, base, own))
            entry["linearSweep"] = True
            entry["functions"] = [{"address": f"0x{a:08X}", "name": f"ptr_{a:08X}"} for a in pointers]
        overlays.append(entry)
        print(f"  {name}: offset={offset} size={length} "
              f"{'symbols' if name in SYMBOL_DRIVEN else str(len(entry['functions'])) + ' discovered'}")

    cfg_path = pathlib.Path(args.config)
    cfg = json.loads(cfg_path.read_text(encoding="utf-8"))
    cfg["overlays"] = overlays
    cfg_path.write_text(json.dumps(cfg, indent=2) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
