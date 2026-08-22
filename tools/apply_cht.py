#!/usr/bin/env python3
"""Apply a DuckStation .cht to GT2's extracted code images.

These cheats patch MIPS instructions. In a static recompilation the MIPS is
translated at build time, so poking RAM at runtime would change nothing - the
patch has to go into the image before it is translated.

Only writes that land inside a code image are applied here. GT2's cheats also
touch strings that the game loads from GT2.VOL into RAM at runtime; those have
no image to patch and are reported so they can be handled separately.

Every A7 write states the halfword it expects to replace, which is checked
before writing. A mismatch means the cheat does not belong to this build, and
is refused rather than written blindly.
"""
import argparse
import pathlib
import struct
import sys

sys.path.insert(0, "tools")
from cht import parse, blocks, word_at, half_at  # noqa: E402

BASE = 0x80010000


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cht")
    ap.add_argument("--in-dir", default="ovl_bin")
    ap.add_argument("--out-dir", default="ovl_patched")
    ap.add_argument("--runtime-out", help="write RAM-only patches here as JSON")
    args = ap.parse_args()

    images = {}
    for path in sorted(pathlib.Path(args.in_dir).glob("*.bin")):
        images[path.stem] = bytearray(path.read_bytes())

    title, codes = parse(args.cht)
    applied, ram_only, refused = 0, [], []

    for master, group in blocks(codes):
        owners = None
        if master is not None:
            owners = [n for n, img in images.items()
                      if word_at(img, BASE, master.addr) == master.value]
            if not owners:
                refused.append(f"master {master.raw}: no image matches")
                continue

        for code in group:
            if code.kind != 0xA7:
                continue
            expect, new = code.value >> 16, code.value & 0xFFFF
            targets = owners if owners else list(images)
            hits = [n for n in targets if half_at(images[n], BASE, code.addr) == expect]
            if not hits:
                # Not in any image: the game loads it into RAM at runtime.
                ram_only.append({"address": f"0x{code.addr:08X}",
                                 "expect": f"0x{expect:04X}", "value": f"0x{new:04X}"})
                continue
            for name in hits:
                struct.pack_into("<H", images[name], code.addr - BASE, new)
                applied += 1

    out = pathlib.Path(args.out_dir)
    out.mkdir(exist_ok=True)
    for name, data in images.items():
        (out / f"{name}.bin").write_bytes(bytes(data))

    print(f"{title}: {applied} code write(s) applied into {args.out_dir}")
    if ram_only:
        print(f"  {len(ram_only)} write(s) target RAM the game fills at runtime")
    for line in refused:
        print(f"  REFUSED {line}")

    if args.runtime_out and ram_only:
        import json
        pathlib.Path(args.runtime_out).write_text(
            json.dumps({"name": title, "patches": ram_only}, indent=2) + "\n",
            encoding="utf-8", newline="\n")
        print(f"  runtime patches -> {args.runtime_out}")


if __name__ == "__main__":
    main()
