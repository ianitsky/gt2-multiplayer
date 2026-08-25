# Race start: what the game's own race looks like from the inside

**Date:** 2026-08-23
**Status:** Findings. No design approved yet.

Phase 2 of the multiplayer alpha is: the race starts at the same moment for
everyone, and every player's car sits in its own grid slot. Nothing beyond that.

Before any of it could be designed, one question had to be answered: how does
GT2 say "run this course with these cars"? This records what was found, how, and
what is still unknown.

## The car id is the packed five-character code

`gt2_main_hash_car_id` is not a hash. It is the same packing the car database
uses:

```
id = t[c0] << 24 | t[c1] << 18 | t[c2] << 12 | t[c3] << 6 | t[c4]
```

five characters, six bits each, most significant first, over
`-0123456789abcdefghijklmnopqrstuvwxyz`. Read from `generated/main.cs` at
`0x80060924`, and confirmed against `.carinfoe`, whose 1110 records decode to
the 1110 filenames in `carobj/`.

**So `Player.Car`, which the lobby already carries as a five-character code, is
byte for byte the identifier the race engine uses.** Nothing has to be
translated.

At boot the game builds a table of `{ u32 id, u16 firstEntry, u16 0 }` at
`0x801DF5D0`, one per car, 1110 of them — confirmed by finding exactly 1110
known ids there in a RAM dump.

## The race is assembled in RAM at 0x801D5850

This is the find that matters. Seven RAM dumps taken while the attract demo ran
all hold the same block, byte for byte:

```
0x801D585E  02 01 01 02 00 01 02 01 02 05 00 01 00 02   fourteen bytes, meaning unknown
0x801D586C  "MSC0002"                                    the race's key
0x801D587C  "Seattle Circuit Full Course"                the course, as shown on screen
0x801D58A0  entrant 0 ────────────────────────────────┐
0x801D5970  entrant 1                                 │  six records,
0x801D5A40  entrant 2                                 │  208 bytes each
0x801D5B10  entrant 3                                 │  (0xD0)
0x801D5BE0  entrant 4                                 │
0x801D5CB0  entrant 5 ────────────────────────────────┘
```

An entrant record, from the demo's first one:

```
+0x00  driver or team name, NUL padded    "General02"
+0x18  u32 car id                          us36n
+0xA8  car name, as shown on screen        "Shelby GT350 '66"
```

The demo's grid was `us36n ulcun ulrrn cc69n ulsbn ulrrn` — six cars, one model
entered twice, on Seattle Circuit Full Course. Everything between the two dumps
taken 30 seconds apart is identical, so this is configuration, not live state.

`0x801D585C` is referenced from about thirty places in `gt2_01` — the race
overlay — which is what a race context base should look like.

**The caveat is now closed — the block is live.** Writing six different car ids
into it and letting the demo carry on put "Replay - Aston Martin V8 VANTAGE" on
the HUD, which is `ldvan`, the first entrant written. The game did not crash and
the race logic ran: lap 1/2, 1st place, lap times counting. So the block is not
a staged copy; it is what the race reads.

What the demo does *not* do is load course or car geometry — it opened
`/font/racefont.dat` and the engine samples `/engine/20403.es`, `ene_n.es`,
`ene_t.es`, and nothing else. The screen showed the HUD over an empty
background. So the block drives the race's bookkeeping; whether it also drives
what gets loaded and drawn is not yet shown.

## Race definitions live in a GTDT container

`carparam/gtmode_race.dat.gz` is a container whose format is now understood:

```
'GTDT' | u16 108 | u16 sectionCount | sectionCount x { u32 offset, u32 size }
```

The section table ends exactly where the first section begins, which is how the
format was confirmed. `usa_arcade_data.dat.gz` uses it too, with 68 sections;
`gtmode_race.dat.gz` has 6.

`gtmode_race.dat.gz` section 3 is a string table — a one-byte length, the
string, a NUL, then the next length — holding 204 race keys (`MSC0002`,
`MRC0001`, `PFL0003`…) interleaved with course codes (`testline`, `circle80`,
`SMtN_License1`). `MSC0002`, the demo's key, is in it. Sections 0 and 1 hold the
per-race data: 3224 records of 12 bytes and 3630 of 32.

`usa_arcade_data.dat.gz` turned out **not** to hold race lineups. Its sections
are per-car parts and tuning catalogues keyed by car id, 64 cars' worth. Arcade
race lineups are somewhere else, most likely in `gt2_03` itself, which carries
the sixteen event codes `A0S A0A A0B A0C … A3C` — four tiers by four classes, in
the order S, A, B, C.

## How the disc is read, which matters for any further probing

The game resolves every path it knows once at boot through
`gt2_ovr0_vol_search_vol_dir`, and opens by index afterwards:

```
// gt2_main_vol_get_file_data_sector_offset(index)
sector = ReadU32(0x801E35F0 + index * 4 + 0x10) >> 11
```

That index is the same value a GTFS entry carries, so it can be named offline —
`tools/vol_index.py` does exactly that, and it is how the engine loads above
were identified. A probe that hooks the by-name lookup sees only the boot
enumeration and nothing during play.

Course and car **geometry** does not go through the by-index accessor either.
`gt2_ovr0_task0b02_carobj_loader` searches `/carobj` once, caches the directory
at `0x800A97D0`, and works from cached entries. Finding where course geometry is
requested is still open.

## What is still unknown

1. **The start.** `gt2_main_func21` installs a race: it copies a **1420-byte
   (0x58C) race definition** from its `A0` into `0x801D585C`. The menu overlay
   reaches it through `func_80011384` -> `func_80020E14` -> `gt2_main_func21` ->
   `gt2_main_func210` -> `memcpy`, found with a write watchpoint on the block.
   So a race is one 1420-byte record, and installing it is one call. What is
   still unproven is whether calling `func21` ourselves, outside the menu's
   flow, is enough to launch - the experiment above rode the demo's own entry
   into the race overlay rather than starting one cold.
2. **The course.** The block names the course in words (`Seattle Circuit Full
   Course`) and by race key (`MSC0002`), but the asset code (`seattle`) is not in
   it. Something maps one to the other; RAM holds a table of all 120 course codes
   at `0x80172380`, and no live pointer into it was found.
3. **Grid slots.** Whether entrant order *is* grid order, and where the starting
   positions come from.
4. **How many entrants.** Six records were seen. Whether six is the maximum, and
   whether fewer is allowed, is untested — it happens to match the lobby's own
   six-player cap.

## Two ways of starting a race that do not work

Both were tried, and both fail for reasons worth keeping.

**Calling the race overlay's entry point from a panel freezes the process.** A
panel's `Draw` runs inside `PumpHost`, which runs inside interrupt delivery, so
calling back into the game from there leaves delivery blocked and the overlay's
own wait loops never advance. The window stops responding and only a kill ends
it.

**Calling it from the lobby hook crashes on a text address.** From the game's own
call path the freeze goes away, but `Dispatcher.Call(c, m, 0x80011F64)` reuses
the live `CpuContext` of `gt2_load_overlay`'s frame, whose registers belong to
that function. The result was `unmapped call: 0x53206174` — ASCII `"ta S"`, a
string being used as a jump target — and then a second failure in
`gt2_load_overlay_Impl` once the wrecked context unwound. **An overlay entry
point cannot be jumped into with an arbitrary register state.**

The block was also empty at that moment: the race key read as blank, because the
menu had not installed a race yet. Writing six entrants into an otherwise zeroed
1420-byte record leaves no course, no lap count and no flags.

## Where the record is built, and by whom

The menu task `func_80011384` in `gt2_02` is a state machine. Its demo branch
does:

```
func_80020DCC(...)                     -> the data blob
func_80020E14(blob, 0x801055C0, index) -> builds the race record
gt2_main_func21(0x801055C0, 0)         -> installs it at 0x801D585C
```

`0x801055C0` is the fixed buffer a race record is assembled in, and `index` is
incremented and wrapped on each pass — which is how the demo cycles races.

Inside `func_80020E14(blob, dest, index)`:

```
race record   = blob + 0x988 + index * 92
entrants      = blob + 0x1580 + entrantIndex * 128
entrant chain = 16-bit next-index list at blob + 0x204
count / first = i16 at raceRecord + 0x52 and + 0x50
```

So a race is a 92-byte definition naming a linked list of 128-byte entrants, and
`func21` turns that into the installed block.

**This is the seam to use.** Nothing has to be synthesized: let the game build a
real race, then substitute. Patching the installed block already works — that is
what put our car on the HUD. A `post` hook on `gt2_main_func21` writing the
room's six car ids into the block is a far smaller change than assembling 1420
bytes by hand, and it leaves the course, laps and flags of a race the game
itself considered valid.

What is still missing is not the record but the **trigger**: on the Simulation
disc, a player reaches a race by navigating GT mode. The demo reaches one on its
own. Finding the state in `func_80011384` that begins a race, and entering it
deliberately, is the next thing to reverse.

## The Combined Disc: one byte away, blocked by video

The Combined Disc exposes arcade mode, which is the front-end phase 2 wants —
a race you reach by choosing a course and a car, rather than by navigating GT
mode. Switching to it costs almost nothing on paper:

- **The boot executable differs by exactly one byte.** `SCUS_944.88` is the same
  628736 bytes on both discs, and the only difference is at `0x8005D704`:
  `addiu a0, zero, 1` becomes `addiu a0, zero, 5`. That is the argument to the
  first `gt2_load_overlay_default` — which overlay the game starts in. Every
  function map, patch and entry point stays valid.
- **The overlays barely move.** `gt2_04`, `gt2_05` and `gt2_06` are byte for
  byte identical; `gt2_01` differs in 6 bytes across 3 places; `gt2_02` differs
  in 288 and grows 124 bytes at its tail; `gt2_03` differs in one 1943-byte run
  at `0x800267F7`. Nothing shifts an address.
- All 45 bytes we patch into `ovl_patched` apply to the Combined images
  unchanged, and `tools/gen_course_table.py` and `gen_car_table.py` produce
  byte-identical output from the Combined `gt2_03`.

`tools/extract_overlays.py` does the whole switch in one command, in either
direction, and carries the patches across.

**And then it black-screens.** The Combined Disc boots into `gt2_06`, which is
the video overlay — its symbols are `DecDCT_inout_caller`,
`dctout_callback_task0`, `decdctout_user0`. It plays an intro from
`STREAM.DAT`, a 335 MB file only the Combined Disc carries, before reaching the
mode selector. Traced, it is not stuck on one wait: it reads sectors and polls
callbacks in a tight loop — `CdGetSector` 24084 times, `CheckCallback` 70323 —
and never advances. The display switches to 320x240 and stays black.

The same disc plays fine in an emulator, so this is a gap in the port, not in
the image: streaming playback is a subsystem GT2's Simulation disc never needed
and this port therefore never exercised.

**The intro turned out to be skippable.** `gt2_ovr6_entrypoint0` is two steps:
play the video, then `gt2_load_overlay_default(1)` — the front-end that offers
Arcade and GT mode. Replacing the first with a no-op (`patches/SkipIntro.cs`,
a `replace` patch on `gt2_ovr6_task10`) leaves the second intact, and the port
then walks `gt2_06` -> `gt2_02` -> `gt2_01`: the same overlay sequence the
Simulation disc takes. The project now runs on the Combined Disc.

**Caveat, recorded because it is not proven.** That the overlays load says the
code advances, not that anything is drawn. Whether the mode selector actually
appears has not been seen by anyone yet.

**And a probe that lied.** A screenshot facility reading `Runtime.Gpu.Vram` at
`DisplayX/DisplayY` reported a black screen — but run against the Simulation
disc, which demonstrably renders, it reported *identical* numbers: display at
0,0, content at x >= 353, 167502 lit pixels. The backend is `Gl45`, so the
VramShadow is not what reaches the screen. Any future screenshot has to come
from the GL backend's own framebuffer, and every conclusion drawn from that
probe's black images was worthless.

## One place installs every race

`gt2_main_func21` is called from exactly two places in the whole game: once in
`main`, and once in the menu overlay `gt2_02`. Arcade and GT mode alike get
their race through it. That makes it the single seam phase 2 needs — one hook
sees, and can substitute, whatever race is about to run.

Watching it while the attract demo ran gave the record's head:

```
+0x00  14 bytes of flags and parameters
+0x10  "MSC0002"                       the race key
+0x20  "Seattle Circuit Full Course"   the course, as shown on screen
+0x40  AE 62 D7 A2                     a u32 sitting between the two
+0x44  entrant 0
```

`+0x44` is exactly `0x801D58A0 - 0x801D585C`, which settles it: the installed
block *is* the record, based at `0x801D585C`.

The u32 at `+0x40` is the obvious candidate for a course identifier, wedged
between the course's name and the grid. It is **not** a plain hash: crc32, djb2,
fnv1a, sdbm and the game's own five-character packing were all tried against
`seattle`, `2p_seattle`, the display name and the race key, and none produce
`0xA2D762AE`. Whatever names the course is still open.

### The demo's races come from the replay file

Hooking `func_80020E14(blob, dest, index)` showed `blob = 0x800E15C0`,
`dest = 0x801055C0`, `index = 0`. Dumped and decoded, the blob begins `'SC'`
followed by Shift-JIS — it is the `.gmr` replay the attract demo plays, and its
"races" are named `Demo 01` through `Demo 06`, each carrying its own entrant
list. So the demo does not read the game's race table at all, which is why
watching it never revealed one.

Finding the arcade race table needs the arcade menus driven by hand with the
`func21` watch armed. That is the next step, and it is the first one in a while
that cannot be done without someone at the keyboard.

## The probe kit, for whoever picks this up

Three probes were used and then removed; they are cheap to rebuild:

- a `pre` hook on `gt2_ovr0_vol_search_vol_dir` logging `A0` as a string — the
  boot enumeration, which is the complete list of paths the game can open;
- a `pre` hook on `gt2_main_vol_get_file_data_sector_offset` logging `A0`, fed
  through `tools/vol_index.py` to get filenames during play;
- a panel that writes the 2 MB of RAM to a file, on a timer so it needs nobody
  at the keyboard;
- a screenshot: the runtime has none, and reading `Runtime.Gpu`'s VRAM through
  `DisplayX/Y/Width/Height` into a hand-rolled PNG takes about fifty lines. Run
  it on **its own thread** — once the game thread is inside a race it may never
  come back, and that is exactly when a picture is worth having.

`ModeHook` also needed a temporary escape — an environment variable that made
the lobby hook stand aside — because with the lobby installed there is no way to
reach the game's own race screens. Anything that drives the game into a race for
observation will need that again.

## Settled by watching a real arcade race (2026-08-25)

The port now plays a full arcade race, so the questions above stopped being
guesses. Watching the race context at `0x801D585C` while choosing a car and a
track, with `GT2_RACE_WATCH=1`, shows this:

**The menu fills the block as you choose.** It is not installed in one call:
the course name changes while you browse tracks, and the entrants appear as the
choice is made. `gt2_main_func21` is never called - that route belongs to the
attract demo, not the arcade.

**Six entrants, and the player is entrant 0.** The player's chosen car sits in
slot 0 and is the only one whose driver-name field is filled (with `"0"`); the
five opponents have it empty. The race key for an arcade race is `A0A`.

**Entrant order is not grid order.** The player, entrant 0, starts *last* - the
HUD reads 6th on the grid. The opponents fill the places ahead.

**The block drives what is on track, not just the paperwork.** Writing
`n24vn` into the five opponent slots after the menu had finished, with
`GT2_RACE_GRID`, put five identical Skyline R34s on the grid. This is the
finding phase 2 rests on: a multiplayer race is this block with the other
players' cars written into it.

**The car name is a separate field and is not derived from the id.** After
overwriting the ids, the names at `+0xA8` still read the cars the menu had
chosen, while the models on track were the new ones. Anything writing entrants
has to write the name too, or the HUD and the track disagree.

Left open: how the course name maps to the asset code, and how to start a race
without going through the menu - though for the alpha the host can go through
it, and the block can be written just before the start.

## The block's real layout, and the two fields phase 2 needs

An earlier reading of this block had the entrant records starting at `+0x44`
with the car id at `+0x18`. They do not. The ids sit at `0x5C`, `0x12C`,
`0x1FC` and so on - a stride of `0xD0` - so a record **begins** at `+0x5C` with
the id at its own `+0x00`. Everything below `0x5C` is header, which is why only
"entrant 0" ever appeared to carry a name: the `"0"` at `+0x44` is the header's,
not an entrant's.

```
header
  +0x10  race key, text            "A0A" for an arcade race
  +0x20  course, text              "Tahiti Road"
  +0x40  u32                       changes with every race; looks like a seed
  +0x5A  u8   how many entrants    6; setting it to 1 runs a one-car race

entrant, 0xD0 bytes, six of them from +0x5C
  +0x00  u32  packed car id
  +0x42  u8   0 for the human, 100 for the rest - an AI skill
  +0x82  u8   0 for the human, 1 for every opponent
  +0x8D  u8   place on the grid, from zero
  +0x90  car name, text            as shown on screen
```

**`+0x8D` is the grid.** Across the six entrants it holds a complete
permutation of 0..5, and the player - who starts sixth - holds 5. Swapping the
player's value with whichever entrant holds 0 starts the player on pole with
all six cars present. Assigning rather than swapping does not work: two
entrants holding the same place is not a grid the game can build.

**`+0x82` says who the human drives.** Which is the other half of a
multiplayer grid: six machines racing the same six cars, each marking a
different entrant as its own.

Two earlier conclusions were wrong and are worth naming, since both were drawn
on the bad offsets. Writing to "the entrant's first 24 bytes" broke the race
because those bytes are the header, and one of them is the entrant count -
a race of one car, which looks exactly like the opponents failing to spawn. And
the "driver name" that seemed to mark the player was the header's text.

### The human drives entrant 0, whatever is marked

`+0x82` is 0 for the player's entrant and 1 for the rest, which looked like it
nominated the car the human drives. It does not. Putting the local player in
entrant 1 and marking that entrant as the human's leaves them driving entrant
0's car: the game takes the human's car by position, and `+0x82` governs
something else - AI behaviour, most likely.

So each machine leads with its own player, and the entrant order differs
between machines by that rotation. The grid place at `+0x8D` comes from the
room's own order instead, which every machine shares, so all of them put each
player in the same place however they number the entrants.

For netcode this means a car has to be identified across machines by *player*,
not by entrant index.

## What the arcade prepares, and why a cold launch dies

Supplying the block and pointing the game at the race overlay is not enough:
the overlay runs and then reads through an object nobody built. Tracing every
file the game reads - each one passes through
`gt2_main_vol_get_file_data_sector_offset` with its index in A0 - shows what
the arcade does between loading its own overlay and loading the race.

195 reads, almost all of them the menu's own furniture:

```
/sound/arcseq.ins
/arcade/arc_carlogo      x190     the car logos, for the selection screen
/carobj/ccrcn.cdo.gz              the player's car
/carobj/ccrcn.cdp.gz
/arcade/course_map                the map picture, for the selection screen
/font/racefont.dat                the HUD font
```

Take the menu's furniture away and the preparation is tiny: **the player's own
car, the race font, and a sound bank**. Nothing else.

Note what is *not* there - the course geometry and the opponents' models. Those
are loaded later, by gt2_04 and gt2_01, from the block itself. That is why
rewriting the opponents' car ids works so cleanly: at that point they have not
been loaded at all.

So a race launched cold most likely dies for want of the player's car object,
which matches the symptom exactly: the race overlay dereferences an object that
was never built.

## What phase 2 looks like from here

Not a design, a direction, and it rests on the block above being sufficient:

1. ~~Prove the block.~~ Done - see above. The remaining unknown is starting a
   race cold rather than riding the demo into one.
2. Fill it from the room. The car ids are already the right identifiers; the
   course needs its asset code resolved to whatever the block wants.
3. Start together. The host sends a start message naming the entrants and a
   deadline; every client assembles the same block and starts on the same tick.
   The lobby's session channel already carries whole-state messages and is the
   obvious place for it.
4. Grid slots by room order, and check each player's own car is in its own slot.

Every machine will run its own simulation. Everyone starts together with the
right cars in the right places, and then drifts — the other cars are driven by
the local AI. Seeing each other move is rollback, which is a later cycle.
