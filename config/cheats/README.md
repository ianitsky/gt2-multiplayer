# Cheats applied into the overlay images

A static recompilation translates the game's MIPS at build time, so a cheat that
pokes RAM at runtime changes nothing. The instruction patches have to go into
the overlay image before it is translated:

```bash
python tools/apply_cht.py config/cheats/<name>.cht --in-dir ovl_patched --out-dir ovl_patched
dotnet run --project RecompOne/RecompOne.Recompiler -c Release -- config/gt2.json
```

**`--in-dir ovl_patched`, not `ovl_bin`.** The images are patched in place, one
cheat on top of the last. Running it from `ovl_bin` rebuilds them from the
pristine disc and silently throws away every patch applied before it.

`ovl_patched/` and `generated/` are both git-ignored, so **the .cht files here
are the only durable record of what has been patched.** That is not yet true of
everything: gt2_01, gt2_03 and gt2_05 carry 45 bytes of an earlier cheat whose
.cht was never kept, and which exist in one untracked directory and nowhere
else. Anything patched from now on belongs here.

`apply_cht.py` applies `A7` codes only - compare a halfword, write a halfword -
and reports the rest. Every `A7` states the halfword it expects to replace, so a
cheat for another build is refused rather than written blindly.

## replay-cameras-in-race.cht

From CookiePLMonster's Console-Cheat-Codes, for NTSC-U 1.1 and 1.2. Its master
code, `A401F888 AEB40008`, matches gt2_01 in this build, and six of its seven
halfword patches match our images byte for byte.

Four `A7` codes are applied, and they are the whole of "extra cameras in a
race":

| address | was | becomes |
| --- | --- | --- |
| 0x8001031C | `li $v1, 3` | `li $v1, 9` - three camera modes become nine |
| 0x80010370 | `jal 0x800103C0` | `jal 0x80011704` - the choice goes to the routine that knows the extra ones |
| 0x8001171C | `lbu (s1 + 0x106)` | `lbu (s1 + 0x10E)` - and it reads the field they live in |
| 0x80011778 | `j 0x800117A4` | `j 0x8001175C` |

The file's other two blocks are not applied. One is the cinematic camera the
cheat holds R1 for, which is three `F5` patches and a byte written into RAM
while the button is down - a condition, and conditions cannot be baked into an
image. The other restores the first block while a real replay runs, which is
this cheat's own admission that the four patches above change what a replay
does. Nothing in this port runs the game's replay today; if something does, that
block is where to look first.
