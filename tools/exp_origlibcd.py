#!/usr/bin/env python3
"""Toggle the original-libcd experiment on or off.

The committed build reimplements libcd in C#. Running the game's own libcd
instead means removing that block from the recompiler's patch table, which is
what this flips. Kept as a script because the experiment is run repeatedly and
the edit is easy to leave half-applied by hand.
"""
import sys, subprocess, pathlib

SDK = pathlib.Path("RecompOne/RecompOne.Recompiler/CodeGen/SdkPatches.cs")
MARK = "/* EXPERIMENT: original libcd\n"


def on():
    s = SDK.read_text(encoding="utf-8")
    if MARK in s:
        print("already on")
        return
    i = s.index('("RecompOne.Runtime.Sdk.LibCd"')
    j = s.index('("RecompOne.Runtime.Sdk.LibEtc"')
    SDK.write_text(s[:i] + MARK + s[i:j] + "*/\n        " + s[j:], encoding="utf-8", newline="\n")
    print("original libcd: ON")


def off():
    subprocess.run(["git", "-C", "RecompOne", "checkout",
                    "RecompOne.Recompiler/CodeGen/SdkPatches.cs"], check=True)
    print("original libcd: OFF (patch table restored)")


if __name__ == "__main__":
    {"on": on, "off": off}[sys.argv[1]]()
