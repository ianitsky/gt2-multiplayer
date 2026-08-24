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

## The probe kit, for whoever picks this up

Three probes were used and then removed; they are cheap to rebuild:

- a `pre` hook on `gt2_ovr0_vol_search_vol_dir` logging `A0` as a string — the
  boot enumeration, which is the complete list of paths the game can open;
- a `pre` hook on `gt2_main_vol_get_file_data_sector_offset` logging `A0`, fed
  through `tools/vol_index.py` to get filenames during play;
- a panel that writes the 2 MB of RAM to a file, on a timer so it needs nobody
  at the keyboard.

`ModeHook` also needed a temporary escape — an environment variable that made
the lobby hook stand aside — because with the lobby installed there is no way to
reach the game's own race screens. Anything that drives the game into a race for
observation will need that again.

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
