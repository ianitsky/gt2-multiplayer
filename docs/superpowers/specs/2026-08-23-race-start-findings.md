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

### How the player's car is asked for

Logging the return address alongside each file index says who asks. The two
carobj reads come from gt2_03, at 0x80016768 and 0x80016820, and both follow
the same shape:

```
A0 = [S1 + 0x04]          the file's index, out of a request record
call 0x8005D74C           -> where the file starts
call 0x8005D79C           -> how big it is
A0 = destination, A1 = [S1 + 0x48], A2 = start, A3 = size
call 0x80015840           reads and inflates it
```

So loading is a queue: S1 walks an array of request records, each carrying a
file index at +0x04. That matches what `gt2_ovr0_task0b02_carobj_loader`
actually does - despite the name it loads no car, it walks the carobj directory
and builds the table of every car's file index, eight bytes an entry with the
index at +0x04.

What remains for a cold launch is to put a request for the player's car on that
queue rather than to call a loader directly. The file index comes from the
table the loader built, keyed by the car's five-character code, which the room
already knows.

### The loader is a state machine, and the index is the archive's own number

`func_80016640(A0 = request, A1 = owner)` in gt2_03 is not a loader called
once. `[request + 0x10]` is a step number, 1 to 8, and the function jumps
through a table at `0x800270A8` to whichever step is due:

```
1  0x80016700   check the request is ready to go
2  0x80016764   read and inflate the model     [request+0x04]
3  0x800167B0   size it
4  0x8001681C   read and inflate the textures  [request+0x04] + 1
5  0x8001686C   size it
6  0x800168F8
7  0x80016944
8  0x800169C0
```

So a request is ticked, not called: each pass advances `+0x10` by one, and a
step that cannot finish yet leaves it alone and returns. The record's fields
that matter so far:

```
+0x04  u16  the file index of the car's .cdo; the .cdp is this plus one
+0x10  u8   which step is due
+0x48  u32  where the model is unpacked to
+0x4C  u32  where the textures are unpacked to
```

The owner is walked by `entry_800141B8` at `0x80014434`: it reads
`[owner + 0x228]` and `[owner + 0x22C]` - **two** request pointers - and ticks
each one every frame. Two slots, which is what a race needs when the player's
car and one other are being brought in.

**The file index is the archive's own directory value.** Resolving
`carobj/ccrcn.cdo.gz` against GT2.VOL offline gives 4091 and
`carobj/ccrcn.cdp.gz` gives 4092 - exactly the two numbers the load trace
caught the game asking for, and consecutive, which is what step 4 assumes when
it adds one rather than looking the second file up. `carobj/` holds 4440 names,
which is the 1110 cars times their four files (`.cdo`, `.cdp`, `.cno`, `.cnp`).

So the room's five-character code resolves to a file index with no table
lookup at all: `carobj/<code>.cdo.gz`, through `VolArchive.TryIndexOf`. The
in-RAM table at `0x801DF5D0` that the boot-time loader builds is the game's own
route to the same number, and is not needed.

What is still unknown is the rest of the record - where a request lives, who
owns it, and where the unpacked bytes are meant to land. Those are runtime
facts, so `LoadTrace` now reports them: a pre-hook runs before the callee
spills anything, so at the moment the game asks for a car file the caller's S1
is the record, S3 the owner and S2 the unpack destination.

### Asking for a car, end to end

Tracing a real arcade race caught the record itself:

```
request at 0x800EF4A8, owned by 0x801FF9F0, unpacking into 0x801FFC14
  +0x04 index 4091   +0x10 step 2   +0x48 model -> 0x800BF4A8   +0x4C textures -> 0x800CF4A8
```

Every one of those numbers is now accounted for.

**The requests are static.** gt2_03 sets them up at `0x80013E8C`: `0x800EF4A8`
and, `0x448` further on, `0x800EF8F0`, installed into `[owner + 0x228]` and
`[owner + 0x22C]` and initialised by `func_80016234`, which copies six buffer
addresses into `+0x40..+0x54` - among them `0x800BF4A8` and `0x800CF4A8`,
matching the trace exactly - and then zeroes `+0x04` and `+0x10`. So the
initialiser prepares a request; it does not ask for anything.

**`func_800162C0(request, owner, packedCarId)` is the ask.** It is the whole
enqueue:

```
0x8005D950(packedCarId)      binary search of the table at 0x801DF5D0,
                             8 bytes an entry, id at +0x00, count at
                             0x801D0000-0x6C38; returns the entry
0x800165E8(request, owner)   cancel whatever the request was doing
[request+0x04] = entry[+0x04]    the file index
[request+0x28] = size of that file
[request+0x30] = size of the next one
[request+0x10] = 1               step 1: go
```

From there `entry_800141B8` ticks both slots every frame through
`func_80016640`, which walks the eight steps and leaves the car in memory.

**So the port does not have to build a loader, resolve an index, or know the
buffer layout. It has to make one call.** The car id it needs is the room's
own five-character code, packed - which `CarInfo` already does.

`VolArchive.TryIndexOf` is what proved this mapping - resolving
`carobj/ccrcn.cdo.gz` offline gave the 4091 the trace had caught - but it is
not on this path, since the game's own table answers the same question and
gives the file sizes too. It stays as a way to check a car exists before
asking the game for it.

**What a cold launch still lacks is the owner.** At `0x801FF9F0` it is on the
main stack, not in static memory: a local of the arcade's own flow, alive for
as long as the arcade is. A launch that skipped the arcade entirely would have
to construct it, and nothing so far says what most of its 0x294+ bytes mean.
A launch that lets gt2_03 initialise and then injects has the owner already.

## Phase 2, as it turned out

Phase 2 was: the race starts at the same moment for everyone, and every
player's car sits in its own grid slot. Both hold, confirmed on two machines
on 2026-08-26.

1. ~~Prove the block.~~ The race is one block in RAM and writing it reaches the
   race.
2. ~~Fill it from the room.~~ The car ids are the same five-character codes the
   lobby carries, so nothing is translated. What each machine *drives* is a
   separate question from what the block *says*: the arcade menus load whatever
   car the local player picked there, so CarLoad asks the game again for the one
   the room agreed on.
3. ~~Start together.~~ A barrier at the moment the race overlay loads. Players
   report in, the host releases them together, and nobody's clock has to match
   anybody else's for a message to say "now". Measured: the host absorbed a
   1.50s difference in load times and both machines were released within the
   resolution of the clock.
4. ~~Grid slots by room order.~~ `+0x8D` per entrant, dealt out in the room's
   order, which every machine shares.

Three things went wrong on the way, and all three were the same shape - a
message that could not arrive, with nothing saying so:

- `HostSaidGo` was set only by `ClientTick`, which nothing calls once the lobby
  has exited. The client waited on a flag nobody could raise.
- The client reported at the line only if discovery could name the host, and
  discovery forgets a host three seconds after its last announcement - which
  the host stops sending when the lobby ends. By the time the race overlay had
  loaded, the address was gone and the client's whole branch was skipped.
- Before either, `SendGo` had nobody to send to.

Each of them looked from the outside like "the race did not start together",
and none of them printed anything. The barrier now reports who it is holding
for, how long it waited, and why it gave up - which is what made the last one
findable in one run instead of three.

## The arcade's own way into a race

`gt2_ovr3_arcade_entrypoint_run_menus_then_load_chosen_overlay` at 0x80011750
is the whole arcade in one function. It runs a menu, reads one byte, and
switches on it to pick one of five exits.

The byte is at **0x801EF5F4** and the table at 0x800267DC, read out of
`ovl_bin/gt2_03.bin`:

```
[0] -> 0x800117E4   load_overlay_default(1) and leave
[1] -> 0x8001184C   the race
[2] -> 0x80011804   load 0x800114E0 with A2=1
[3] -> 0x80011828   load 0x800114E0 with A2=0
[4] -> 0x800117D4   load 0x8001172C - gt2_ovr2_start_replay
```

Watching the byte through a real arcade run shows it going 0 then 1, and 1 is
the race. So **1 is the lever**.

The race case does this, and only this:

```
0x8007D23C, 0x80083AE0 x2      seeds
0x80010C84(..., 0x801C3350)    builds an object there
0x80014898(SP+0x10)            enters a second screen
loop on gt2_main_call_vtable_slot_0c_and_report_zero    car and track
0x800148CC(SP+0x10, 2)
copy 0x2D0 bytes from 0x801C3350 to 0x801D5FA0
gt2_load_overlay_default(3)
gt2_load_overlay(0, 0x80011F64, 0)      the race
```

### The menu loops are not spins

Both loops read as "keep calling until the answer is at least two", which would
never end, since the function called always reports zero. The comparison is the
other way round: the loop repeats **while** the answer is two or more and falls
through on zero. So one call is enough to leave.

What runs the menu for all those frames is the function reached through the
vtable, which does not return while the screen is alive - it longjmps, which in
this game is a task switch. The loop falls through once the screen is genuinely
finished.

That matters for skipping the menus: there is no result value to fake. Getting
past a screen means not entering it.

### What a direct launch would have to replay

Every step above is visible, so a launch that skips the menus is a known
sequence with the two screens removed rather than a new thing to discover. The
owner of the car loader, which was the blocker, stops being one: the arcade's
own initialisation builds it before either screen runs.

The risk that remains is what the screens leave behind. They draw, but they
also fill state, and the 0x2D0 bytes copied out of 0x801C3350 are assembled
somewhere. If part of that only exists because a screen ran, a direct launch
dies the way the old one did - but this time the place to look is named.

### The screens contribute nothing to the 720 bytes

Captured from a real arcade race: the object at 0x801C3350 the moment
`gt2_ovr3_arcade_build_race_parameters_block_720_bytes` finished building it,
and the 720 bytes at 0x801D5FA0 the moment the race overlay loaded. **They are
byte for byte identical** - 212 non-zero bytes in each, in the same places.

So the car and track screen does not touch this object at all. The player's
choices go into the race block at 0x801D585C, which is a different thing; this
one is built once, before the screen runs, and copied out unchanged after it.
The risk that a direct launch would be missing state the screens filled in does
not apply here.

The source reads as all zeroes by the time the overlay loads, which is after
the copy - something clears it between the two. That is a curiosity rather
than a problem, since the copy has already happened by then.

### The screens are objects with vtables

`gt2_ovr3_arcade_construct_car_and_track_screen` at 0x80014898 is a
constructor: it calls 0x8007FE8C and writes the vtable 0x80027010 into the
object. That vtable, read out of the overlay image:

```
+0x04 0x80015620   +0x08 0x800148CC   +0x0C 0x80083418   +0x10 0x800148F4
+0x14 0x80080A24   +0x18 0x80080B10   +0x1C 0x80014B68   +0x20 0x80080C94
+0x24 0x80014B88
```

Slot 0x0C is the one `gt2_main_call_vtable_slot_0c_and_report_zero` reaches,
and it is 0x80083418 in main - a generic run-a-screen method, not something
specific to this screen.

Still unsettled: where the car loader's owner is built. The code that installs
the two request records is at 0x80013E8C inside func_80013BE4, which nothing
calls by address - it is reached through a pointer, and it is not in the vtable
above. Whether it runs before the screen a direct launch would skip, or as part
of it, decides whether skipping costs the owner again.

### The owner is built before any screen, and the direct launch is unblocked

Timed through a real arcade race:

```
1. 00.471  the arcade overlay's entry point runs
2. 00.475  the screen object is initialised and the request records
           installed              (A0 = 0x801FF9F0)
3. 00.808  the car loader ticks for the first time - owner 0x801FF9F0
4. 07.061  the 720-byte race parameters block is built
5. 07.085  the pre-race screen is constructed  (object at 0x801FF9F0)
6. 07.612  the race overlay is loaded
```

Step 2 lands **four milliseconds** after the entry point and six and a half
seconds before any screen the race case constructs. So the car loader's owner
comes out of the arcade's own initialisation, not out of a screen: a launch
that skips the screens keeps it. That was the last thing standing between here
and a direct launch.

Note that the owner and the screen object are **the same address**. There is
one object, at the entry point's `SP+0x10`, reconstructed with a different
vtable as the arcade moves on - so "the loader's owner" and "the arcade's
current screen" are the same thing seen from two sides.

### A name the clock disproved

0x80014898 was called the car and track screen, from where it sits in the race
case. The timing says otherwise: it is constructed at 07.085 and the race loads
at 07.612, half a second later, which is not long enough to choose a car and a
track. Whatever the player chooses happens earlier, in the first screen - the
six seconds between steps 3 and 4.

It is now named for what is certain about it: the screen the race case
constructs, with vtable 0x80027010, just before loading the race.

### Loading an overlay takes two arguments that must agree

`gt2_load_overlay(index, entryPoint, ...)` is told the same overlay twice.
The index picks the bytes to decompress; the entry point is where to jump
afterwards, and it is also what the port keys its own function map on.

The table of entry points is at 0x80091174, a flat array of words, read out of
SCUS_944.88:

```
[0] 0x80012254  gt2_01     [3] 0x80012C00  gt2_04
[1] 0x80011384  gt2_02     [4] 0x80013628  gt2_05   Simulation
[2] 0x80011750  gt2_03     [5] 0x800114B8  gt2_06
                Arcade
```

Redirecting one without the other is what the first direct launch did: it set
the entry point to the arcade and left the index at Simulation's. The game
decompressed Simulation's bytes; the port switched its function map to the
arcade; the arcade's code then read Simulation's data and followed a pointer to
0x7ED37C30, which is not RAM.

It surfaced as data corruption inside gzip rather than as a mismatch, and it
took the arcade's own body down as well as the direct launch - which is how it
was found: a fallback that dies where the thing it replaces died is not a
failure of the replacement.

## What is still open

- **The two hitches**, at the end of the countdown and the end of the race.
  About one to three seconds, a complete freeze, recovers on its own. Not the
  task scheduler: that had a real defect, it was fixed, and these survived it.
  Deliberately parked.
- **Skipping the arcade menus.** `func_800162C0` loads a car and is proved, but
  a launch that skips the arcade has no owner to pass it - the owner is a local
  of the arcade's own flow on the main stack. Riding the arcade and injecting
  works; launching cold needs that object built.
- **The course.** The room's track does not propagate: the course name has not
  been mapped to its asset code, so everyone races wherever the host went in
  the menus.
- **Seeing each other move.** Every machine runs its own simulation, so the
  cars start right and then drift - the opponents are driven by the local AI.
  That is rollback, and a later cycle.

Every machine will run its own simulation. Everyone starts together with the
right cars in the right places, and then drifts — the other cars are driven by
the local AI. Seeing each other move is rollback, which is a later cycle.
