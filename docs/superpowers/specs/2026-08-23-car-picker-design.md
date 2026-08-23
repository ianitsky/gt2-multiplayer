# Car picker: the arcade classes, and room to invent your own

**Date:** 2026-08-23
**Status:** Approved, not yet implemented

## Problem

The lobby takes the car as free text. There are 1110 cars on the disc, so no list
can be flat and no text box can be right.

Arcade mode already solved this: it groups its cars into classes, and a race
picks one class. The host chooses the class when creating the room, and every
player then picks from the ten-or-so cars in it.

## What the game already knows

**The groups.** `gt2_03` holds a table of pointers at `0x420e8` to five arrays of
car codes — 10, 8, 9, 9 and 24 entries. Alongside them the overlay carries the
sixteen arcade event codes `A0S A0A A0B A0C A1S … A3C`: four tiers by four
classes, in the order S, A, B, C. That order matches the arrays, and the contents
confirm it — the ten are Viper GTS, Shelby Cobra, Vector M12, RUF CTR2; the last
nine are Alto Works, Move Aero-C, 206 S16. The fifth array is the 24 rally cars.

| group | cars | id |
|---|---|---|
| Special | 10 | `special` |
| A | 8 | `a` |
| B | 9 | `b` |
| C | 9 | `c` |
| Rally | 24 | `rally` |

**The names.** `.carinfoe` in `GT2.VOL` is the car database: a `CAR\0` header, a
count of 1110, then 1110 eight-byte records, then a block of per-car fields.

A record is a packed code and an offset:

```
+0  u32  the 5-character asset code, 6 bits per character, most significant
         first, over the alphabet "-0123456789abcdefghijklmnopqrstuvwxyz"
+4  u16  offset of this car's field block
+6  u16  a field this reader does not use
```

Record 0 decodes to `a-a7r`, which is the first entry in `carobj/`. All 1110
codes are distinct.

A field block runs from its own offset to the next record's, and holds several
length-counted strings after a binary prefix whose length varies. The display
name is the **last** counted string in the block: scan backwards for a byte whose
value equals the number of bytes after it. Some names begin with a `0x7f` marker
byte, which is counted in the length and dropped when read.

That rule resolves 1087 of the 1110 cars, and **all 60 arcade cars**. A car whose
name will not parse falls back to its code, the same way an unknown course code
does.

## Architecture

```
build time                       runtime
----------                       -------
ovl_bin/gt2_03.bin               config/car-groups.json (optional)
      |                                   |
tools/gen_car_table.py                    |
      |                                   v
patches/multiplayer/CarTable.cs -----> CarCatalogue  <----- CarNames
   (the five arcade groups)                |                   |
                                           |            .carinfoe via VolArchive
                                    MultiplayerPanel
```

Same split as the course picker, and for the same reasons: the roster is
generated from an overlay that lives in the repository, and the names come off
the player's own disc at runtime, in the disc's own language.

### Components

| component | responsibility |
|---|---|
| `Multiplayer.CarInfo` | parses `.carinfoe`: code to display name |
| `Multiplayer.CarTable` | generated: the five arcade groups |
| `Multiplayer.CarCatalogue` | the groups actually on offer, built-in plus custom |
| `tools/gen_car_table.py` | emits `CarTable.cs` from `gt2_03` |

## Room to invent your own

The point of a separate `CarCatalogue` is that the arcade five are a **default,
not a fixed set**. A group is nothing but an id, a display name, and a list of
car codes, and any of the 1110 codes is a valid member.

`CarCatalogue` reads `config/car-groups.json` when it exists:

```json
[
  { "id": "kei", "name": "Kei cars", "cars": ["x2a8n", "q2mcn", "m2a5r"] },
  { "id": "c",   "name": "Class C (house rules)", "cars": ["fp26n", "gotin"] }
]
```

- A group whose id is new is added after the built-in ones.
- A group whose id matches a built-in one **replaces** it, so a house version of
  Class C is a two-line file rather than a fork.
- A car code that is not in the database is dropped, with a line on the console
  naming it. A group left empty is dropped too.
- A malformed file is ignored entirely, with one console line. The lobby opens
  with the arcade five; it never fails to open because a config file is wrong.

No editor, no UI for this — it is a file the user writes. The alpha ships with
the arcade five and nothing else.

## What travels on the wire

`Room` gains `CarGroup`, the group's **id**. `Player.Car` carries the car
**code**. Both are identifiers, not display text, for the same reason `Track`
already is: the race cycle needs them and they are stable.

This changes the room wire format, so its version goes from 1 to 2 and a version
1 packet is rejected. Both ends of a LAN game are the same build; there is no
compatibility to keep, and silently misreading a field is worse than refusing it.

A client shown a group id it does not have — a room hosted by someone with a
custom `car-groups.json` — displays the raw id and cannot pick a car. It sees the
room and the players; it just cannot join the grid. That is honest, and it is the
same degrade path an unknown course code takes.

## The screens

**Create** gains a group selector under the course grid: the groups in catalogue
order, one row, the chosen one marked. Choosing a group sets `Room.CarGroup`.

**Lobby** replaces the "My car" text box with the group's cars — name, in
catalogue order, the chosen one marked. Ten items needs no search box and no
grid. Picking one sets `Player.Car` to its code, which the session already
retransmits.

No car pictures. `carobj` holds 3D models, not thumbnails, and rendering them is
a different project.

## Failure handling

**No disc, or `.carinfoe` unreadable.** Every car shows as its code. The picker
still works — the codes are stable and unique — it is just unfriendly. The same
rule the course picker uses for a missing map.

**A group with no cars.** Dropped from the catalogue before it reaches a screen,
so no screen has to handle an empty list.

**A room whose group this build does not have.** Covered above: shown raw, not
pickable.

## Testing

- **`CarInfo`** — against a database built in the test: the packed-code alphabet
  and bit order, the counted-string name, the `0x7f` marker, a truncated file, a
  code that is not there. Not against the real disc, which is not in the
  repository.
- **`CarCatalogue`** — the merge rules, each one: a new group appended, a
  matching id replacing, an unknown code dropped, an emptied group dropped, a
  malformed file ignored.
- **`CarTable`** — the generated file holds five groups of 10, 8, 9, 9 and 24,
  ids unique, every code non-empty. This pins the generator against a
  regeneration that produces nothing.
- **`RoomState`** — version 2 round-trips the group, a version 1 packet is
  rejected, and the worst case still fits one datagram.
- **The screens** are not tested automatically. Immediate-mode ImGui; the
  criterion is visual.

## Out of scope

- Putting the chosen car into a race. Nothing acts on the choice yet.
- Tuning, colours, or anything else per-car.
- A UI for editing groups. The file is the interface.
- Restricting groups by course. Rally cars on tarmac is the user's business.
