# Multiplayer backlog, agreed 2026-08-30

Car rotation now works on both machines, so the next round of work is about the
race being the same race everywhere - the same moment, the same colours, the
same visible car behaviour - and about the way in and the way out of it.

Six items, agreed with the player, to be done one at a time and in this order.
Each one is closed only when it has been seen working on two machines.

## 1. The grid appears at the same moment on every machine

**Status: fixed in code, not yet seen working on two machines.**

This worked once and does not now. The measured evidence is in `host.log` and
`client.log` from the run of 21:06 on 2026-08-30:

```
client   [line]  21:06:04.695  the race's first frame - holding the room here
client   [start] 21:06:04.696  holding for 2 players as client
client   [start] 21:06:04.697  the host said go (waited 0,00s)
host     [line]  21:06:06.806  the race's first frame - holding the room here
host     [start] 21:06:06.807  holding for 2 players as host
host     [start] 21:06:06.892  everyone is at the line - go (waited 0,08s)
```

The two machines began 2.111 seconds apart with a barrier standing between
them that both walked straight through.

**Root cause.** The lobby's "the race is on, leave the lobby" and the race's
"everyone is at the line, start now" are the same datagram - `A5 02` - on the
same socket, and neither `LanSession.HostSaidGo` nor `_atTheLine` is ever
cleared between the two moments. So:

- the client reaches the barrier with `HostSaidGo` already true from the
  lobby, and releases itself on the first pass without waiting for anyone;
- it sends exactly one `AtTheLine` on that pass and then leaves, and the host
  - arriving 2.1s later - collects that stale report and believes the room is
  assembled.

Both halves of the handshake are satisfied by messages that were about
something else.

**The fix.** The lobby and the race now say different things. `A5 02` still
ends the lobby; `A5 04` starts the race, and only the barrier sends or hears
it. Three is skipped because a car's place already uses it under the same
magic, and a place must never release a machine that is still holding.

Each barrier also opens its own line: `LanSession.OpenTheStartLine` clears the
reports, lowers the flag, and drops whatever is already sitting in the socket -
without the last of those the first collect would undo the reset. Dropping live
reports is free, because a player at the line repeats theirs every frame until
it is answered.

What a working barrier prints is a wait of about the load skew - two seconds,
not two hundredths. If it prints a short wait and the two machines still start
apart, then the line works and is in the wrong place, and `GT2_RACE_PHASES`
says where the next one goes.

## 2. Start goes straight to loading, not through the arcade menu

**Status: written, not yet seen on screen.**

The arcade has to run. Its first screen's step method is what ticks the car
loader through its eight steps, so the screen stays up until the room's car is
in memory - 1.9s in the measured run, all of it a menu the player did not open
and cannot use.

So the screen keeps running and only the picture goes.

**Not by blanking the console.** GP1(03) is the display-enable bit and the game
owns it: `gt2_main_gpu_set_display_enable` (0x8007F830) is the only function in
the whole game that writes it, and the display environment goes up again every
frame, so a curtain held there would be lifted and redrawn all the way through.
`HostWindow.OutputHidden` is held by the host instead, where the game cannot
reach it, and the GPU carries on drawing into VRAM behind it - the hidden
frames are real frames, and nothing has to be caught up when it lifts.

`ArcadeCurtain` raises it in `DirectRace.Expect`, which runs while the lobby's
own overlay hook is still on the stack, so the arcade never gets a frame on
screen at all. It drops it where `DirectRace` ends the arcade screen, which is
the game moving to its own pre-race screen - the loading step, and the one the
player is meant to see.

In its place, a small centred window says what is being fetched and which of
the loader's eight steps it is on. That window is also what lifts a curtain
nobody else lifted: it draws every frame whatever the game is doing, so a path
this did not expect cannot leave the screen black.

`GT2_SHOW_ARCADE` puts the arcade back on screen, which is the only reason to
want those frames.

## 3. Car colour is chosen in the lobby

**Status: written, not yet seen on two machines.**

### Where a car's paints live

`.carinfoe` is not only the name table. Each of its 1110 records is eight
bytes: the packed five-character code, then one word this port used to read as
a halfword offset and a spare halfword. It is not a spare.
`gt2_main_carinfo_block_and_paint_count_for_car` splits that word as

```
offset = word & 0x3FFFF          the car's block, from the start of the file
paints = ((word >> 18) & 0x1F) + 1
```

and the block begins with the paints, not with the name:

```
paints x u16   the swatch, five bits a channel, red lowest
paints x s8    the letter the game names that paint by
u8 len, text, NUL   the name, which is what this port already read
```

Read against the real disc: the Viper GTS comes in three, the RUF CTR 2 in
twelve, the Shelby GT350 '66 in five, and unpacking the halfwords as
`R = v & 31, G = (v >> 5) & 31, B = (v >> 10) & 31` gives silver, grey, black,
red, yellow, greens and blues - which is what a car's colour list looks like.

### What the race wants

Two different things, in two different places, and it matters which is which.

The arcade's 720-byte parameter block carries the player's paint at **+0x16 as
an index** into that car's list.
`gt2_ovr3_build_race_block_and_fill_all_six_entrants` hands the index and the
car id to `gt2_ovr3_read_signed_byte_from_decoded_car_info_at_offset`, takes
the **letter** that comes back, and writes it - sign-extended to a word - into
each entrant at **+0x04**.

So `DirectRace` writes the index at +0x16 for this machine's own player, before
the builder runs, and `RaceGrid` writes the letter at +0x04 for all six
entrants, after it. Every machine writes every entrant, which is what makes the
six cars the same six colours everywhere.

What is not yet proven is that a letter written after the builder still reaches
what is drawn. The car **id** written at the same moment does - remote cars
already show the right models - so the paint beside it very likely does too,
but "very likely" is what a run is for.

### In the lobby

`Player` gained a `Colour`, defaulted so nothing that does not care had to
change. It travels as the index rather than the letter because the lobby is
where a car can still change, and an index is what has to be re-checked when it
does - `Session.SetCar` takes it back to the first paint on a real change, and
leaves it alone when the player re-picks the car they already had.

The room state is at version 3 and a client's intent at version 2; both carry
one more byte.

The picker draws the swatches themselves. There are no colour names on the
disc - only the swatch and the letter - so squares are the whole of what can
honestly be shown, and each player's own square sits beside their name in the
room list.

## 4. Viewers

**Status: agreed, being read for. Nothing written.**

### What was agreed

A seat in the room that is not a car. Settled with the player on 2026-08-30:

- **The camera** is the game's own replay presentation, and the viewer chooses
  which driver it follows.
- **Viewers do not take grid slots.** A room can hold six drivers and viewers
  besides.
- **Chosen in the lobby**, and changeable between races once item 5 puts
  players back in the lobby afterwards.
- **Live.** The race happens now; "replay" is the presentation, not a
  recording.

Decided without asking, and open to being overruled: a viewer readies up and is
waited for at the start line like anyone else in the room; a room needs at least
one driver; a viewer starts out following the first driver.

### What that costs structurally

Today a player's seat in the room and their place on the grid are the same
number. With viewers in the room they stop being the same, and every pose on
the wire is keyed by that number - so the room has to be split into **drivers**
(the ones with a car, capped at six, and the list a seat counts along) and
**viewers**, before anything else is written.

### What the game says so far

**The kind of race is one byte**, at race block +0x0A. `gt2_01` reads it in 64
places and branches on values from 0 to 11. An arcade race holds **4** - the
builder copies it out of the parameter block's +0x02. The attract demo, which
is a replay, held **2**.

**The race changes its own kind at runtime.** `0x80017098` and `0x8001710C` are
a save-and-restore pair: the save stashes three fields of the block, cuts the
entrant count at +0x5A to one, and clears +0x8C on every entrant after the
first; the restore puts the three fields back and writes a kind the caller
supplies into +0x0A. That is the shape of "go into a one-car presentation and
come back", which is what a post-race replay is. Which kind the caller supplies
is held in a register from further up, and that is where static reading has
stopped.

**A lead on the thing `SecondDriver` never resolved.** Entrant **+0x8C**: the
arcade's own builder writes 1 into it for every entrant, and the one-car setup
above clears it on all but the first. "Which pad drives this entrant" would
behave exactly like that, and it is the flag the port has been looking for
since it started asking what makes an entrant answer to a controller.

### The question that decides the approach

Whether the game's replay kind can run without a recorded input stream. If it
can, a viewer is a normal race with one byte changed and no entrant driven by a
pad. If it cannot, the viewer runs an ordinary race with every car moved by the
poses already on the wire, and the replay is made rather than borrowed: an
external camera on a chosen car, and no driver's HUD.

The camera target has not been found either way, and "the viewer chooses who to
follow" needs it.

## 5. The end of a race returns to the lobby

Not to the arcade menu. The room outlives the race, so a second race can be
started from the same room.

## 6. The other cars behave visibly like cars

Acceleration, braking, wheel rotation and the rest of the visible effects
should look on the remote machines the way they look on the owner's. Place and
heading travel already; this is the rest of what is drawn.
