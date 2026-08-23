# Course picker: the game's own two-player track roster, drawn from its own maps

**Date:** 2026-08-23
**Status:** Approved, not yet implemented

## Problem

The room-creation screen takes the track as free text. Nobody can pick a track
by typing `2p_mountain`, and nothing stops two players agreeing on a track that
does not exist. The lobby needs a real picker before the race cycle can use the
choice for anything.

## What the game already knows

Both halves of this exist on the disc. Neither is guesswork, and neither needs a
list maintained by hand.

**The roster.** `gt2_03` carries four course tables as arrays of 32-byte
records — normal, reverse, and the two with the arcade unlockables — plus two
more for two-player: 21 tarmac courses and 6 dirt. Each record holds a pointer
to the asset code and a pointer to the display name:

```
+0x04  class
+0x08  index
+0x10  char* asset code     "2p_mountain"
+0x14  char* display name   "Trial Mountain Circuit"
```

The two-player tables are the game's own answer to which courses work with two
players, so the roster is taken from them rather than decided here. Reading them
also settles pairings no one would guess: `speed2p` is Super Speedway, not High
Speed Ring, and Grindelwald's two-player asset is called `Gtest`.

**The maps.** `GT2.VOL` is a GTFS archive holding every asset the game loads.
`crsmap/<code>.tim.gz` is a 96×96 4bpp TIM of each course's outline, about 2 KB
packed — one per course, including every two-player variant.

The archive's layout, worked out from the image and recorded in
`tools/gt2vol.py`: a table of u32 offsets storing each file's **end**, so file
`v` runs `offsets[v-1] .. offsets[v]`; every file begins on its own 2 KB sector,
and `offsets[v-1]` lands inside that sector rather than on it, so the start
rounds down. Verified by decompressing all 5114 gzip members, 652 bytes to
336 KB.

## Architecture

```
build time                          runtime
----------                          -------
ovl_bin/gt2_03.bin                  disc image (already open elsewhere)
      |                                   |
tools/gen_course_table.py             VolArchive  - GTFS over DiscFs
      |                                   |
patches/multiplayer/CourseTable.cs    crsmap/<code>.tim.gz
      |                                   |
      |                                  Tim - TIM to RGBA
      |                                   |
      '----------> MultiplayerPanel <--- CourseMaps - RGBA to GL texture, cached
```

The roster is generated at build time because it never changes for a given disc
and a generated file can be read in review. The maps are read at runtime because
extracting them at build time would mean carrying game assets in the repository.

### Components

| component | responsibility |
|---|---|
| `tools/gen_course_table.py` | emits `CourseTable.cs` from `gt2_03` |
| `Multiplayer.CourseTable` | generated: code, display name, surface |
| `Multiplayer.VolArchive` | reads one file out of GT2.VOL |
| `Multiplayer.Tim` | 4bpp/8bpp TIM to RGBA |
| `Multiplayer.CourseMaps` | code to GL texture, decoded once |

This mirrors `tools/gen_overlay_map.py` → `patches/OverlayEntryPoints.cs`, which
already generates a table from an overlay image the same way.

### Finding the tables

The generator does not hardcode the record offsets. It scans `gt2_03` for 32-byte
records whose code pointer lands in the two-player string block, groups the hits
by a stride of 32, and takes the blocks it finds — which is how the roster was
located in the first place, and which fails loudly rather than silently if the
image changes. Exactly two blocks are expected, of 21 and 6 records; anything
else is an error, not a warning.

Surface comes from which block a course is in: the 6-record block is dirt.

## The picker

The create screen replaces its track text box with a grid of the courses, each
cell the 96×96 map above its display name, tarmac and dirt under separate
headings. The selected cell is outlined. Nothing else about the screen changes.

The maps are line art on transparency, so they are tinted with the current text
colour rather than drawn raw — otherwise dark lines vanish against a dark theme.

## What travels on the wire

`Room.Track` carries the **asset code**, not the display name. It is the
identifier the race cycle will need, it is stable, and it is short. The display
name is resolved locally through `CourseTable`.

A client that receives a code it does not know shows the raw code rather than
dropping the room: a future disc revision adding a course should degrade to
something readable, not to a lie.

## Failure handling

Three things can actually go wrong, and each has one answer:

**No disc, or the archive is unreadable.** `CourseMaps` yields no texture and
the grid draws name-only cells. The picker still works; it is just plainer. The
lobby must not fail to open because a picture is missing.

**One map is missing or malformed.** That cell draws name-only. The other 26 are
unaffected — a per-course failure stays that course's problem.

**A TIM that is not 4bpp or 8bpp.** Rejected, treated as missing. The reader
does not guess at 16bpp or 24bpp because no course map uses them.

Decoding is lazy and cached: a course's map is read and uploaded the first time
its cell is drawn, then kept. Twenty-seven 96×96 textures is about a megabyte.

## Testing

- **`VolArchive`** — against a GTFS image built in the test: the end-offset rule,
  the sector rounding, nested directories, a missing path, a truncated archive.
  Not against the real disc, which is not in the repository.
- **`Tim`** — a hand-built 4bpp TIM with a known palette decodes to known pixels;
  8bpp likewise; a 16bpp header is rejected; a truncated file is rejected without
  throwing.
- **`CourseTable`** — the generated file holds 27 courses, 21 tarmac and 6 dirt,
  codes unique, and every code non-empty. This pins the generator's output
  against an accidental regeneration that produces nothing.
- **The grid** is not tested automatically. It is immediate-mode ImGui; its
  criterion is visual.

`tools/gen_course_table.py` is re-run by hand, like the other generators, and its
output is committed.

## Out of scope

- Loading the course for a race. The picker records a choice; nothing acts on it
  yet.
- The car picker. Same shape of problem, same tables, next cycle — `.carinfoe`
  is already readable through the same archive reader this builds.
- Reverse variants and the single-player-only courses. The roster is what the
  game's two-player tables list.
