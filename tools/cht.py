#!/usr/bin/env python3
"""Parse DuckStation-format .cht files and locate each write.

Code types used by these GT2 cheats (from duckstation/chtdb cheat-format.txt):

  A4aaaaaa vvvvvvvv  master code: apply the following codes while the word at
                     0x80aaaaaa equals vvvvvvvv, until a `00000000 FFFF` line
  A0aaaaaa vvvvvvvv  apply only the NEXT code if the word there equals vvvvvvvv
  A7aaaaaa oooonnnn  if the halfword at 0x80aaaaaa is oooo, write nnnn
  90aaaaaa vvvvvvvv  write a word
  80aaaaaa 0000vvvv  write a halfword
  30aaaaaa 000000vv  write a byte
  00000000 FFFF      end of a master-code block

The master code matters here beyond gating: every GT2 overlay loads at the same
address, so the word it tests is what says WHICH overlay a block belongs to.
"""
import re
import struct

LINE = re.compile(r"^\s*([0-9A-Fa-f]{8})\s+([0-9A-Fa-f]{4,8})\s*(?:#.*)?$")


class Code:
    def __init__(self, kind, addr, value, raw):
        self.kind, self.addr, self.value, self.raw = kind, addr, value, raw

    def __repr__(self):
        return f"{self.kind:02X} 0x{self.addr:08X} {self.value:08X}"


def parse(path):
    """Return (title, [Code]). Comments and blank lines are dropped."""
    title, codes = None, []
    for line in open(path, encoding="utf-8", errors="replace"):
        stripped = line.strip()
        if stripped.startswith("["):
            title = stripped.strip("[]")
            continue
        m = LINE.match(line)
        if not m:
            continue
        word, value = int(m.group(1), 16), int(m.group(2), 16)
        kind = word >> 24
        addr = 0x80000000 | (word & 0x00FFFFFF)
        codes.append(Code(kind, addr, value, stripped))
    return title, codes


def blocks(codes):
    """Split into (master, [codes]) groups. master is None outside any block."""
    out, current, master = [], [], None
    for code in codes:
        if code.kind == 0xA4:
            if current:
                out.append((master, current))
            master, current = code, []
        elif code.kind == 0x00 and code.value == 0xFFFF:
            out.append((master, current))
            master, current = None, []
        else:
            current.append(code)
    if current:
        out.append((master, current))
    return out


def word_at(image, base, addr):
    off = addr - base
    if off < 0 or off + 4 > len(image):
        return None
    return struct.unpack_from("<I", image, off)[0]


def half_at(image, base, addr):
    off = addr - base
    if off < 0 or off + 2 > len(image):
        return None
    return struct.unpack_from("<H", image, off)[0]
