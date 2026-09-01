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

### What running it answered

Two switches, run apart, on 2026-08-30.

**`GT2_RACE_KIND=2`: the race loaded and went straight back to the arcade
menu.** Kind 2 is the attract demo's kind, and a race handed it without the rest
of what the demo sets up ends itself before it draws anything. So the game's own
replay cannot be entered by writing one byte, and the replay has to be made
rather than borrowed.

**`GT2_NOBODY_DRIVES=1`: an ordinary race, with the player in full control of
their car.** Every entrant was marked as one the game drives, and the pad still
steered entrant 0. **IsAi is not what binds a pad to a car** - it decides who
steers a car nobody is steering, not whether anybody is. That is the third
outcome `SecondDriver` was written to look for, arrived at from the other end,
and it has been corrected there.

### What a replay actually is

**Found, and running.** A replay is two things, in two places, and four wrong
answers were spent before the second place was looked at.

**The record**, at 0x801D585C. Beyond content, a demo's race and an arcade race
differ in five fields:

```
+0x04   1, where an arcade race has 0
+0x09   1, where an arcade race has 0
+0x0A   2, the kind - an arcade race is 4
entrant +0x82:  C0 41 41 41 41 41,  where an arcade race has 00 01 01 01 01 01
```

Bit 6 of +0x82 is the one
`gt2_ovr3_build_race_block_and_fill_all_six_entrants` sets from an argument the
arcade passes as zero; bit 7, on the first entrant only, is what nothing but a
replay has.

Writing the kind alone had loaded a race and walked straight back out to the
menu, and `gt2_main_func21` - the installer, at 0x80069AC4 - says why: it copies
0x58C bytes into the block, then reads the kind out of what it has just copied
and branches on it. The kind does not switch anything on. It says what shape the
rest of the record is in.

**And the race context**, at 0x800A9500. All five record fields, applied and
still there at the first frame, came up as an ordinary race - so the record is
not what decides the presentation. Two frames into a demo's replay and two into
an arcade race, the head of the race context differs in **forty bytes of 1792**,
and thirty-six of those are inside car 0, which is position and physics. What is
left is `0x800A9500` and `0x800A951C`: one in a replay, zero in a race.

Holding those two at one, every frame, shows the replay.

### Two things it is not

Both measured rather than assumed, and both worth keeping so the next attempt
does not repeat them.

**Not the address the replay-camera cheat gates on.** `0x800A92BC` reads zero on
an ordinary race *and* zero on the demo's replay. The likely reason is the disc:
a Combined Disc merges Arcade and Simulation by patching the boot executable, and
a cheat's RAM address for the official 1.1 and 1.2 need not survive that. Its
overlay patches did, gt2_01 being the same binary either way.

**Not another class.** gt2_01 names four race loops - `15RaceDevelopment`,
`12RaceMenuLoop`, `12RaceViewLoop`, `14ArcadeRaceLoop`, `19GranTurismoRaceLoop` -
and `RaceViewLoop` looked like the answer for about a minute. A name follows its
vtable rather than precedes it, so `12RaceMenuLoop` is 0x8002EF98, whose slot
0x10 is this port's own first-frame hook - and that hook fires during the demo. A
replay and a race come up through the same loop.

### What survives

The pad stays on entrant 0 and the camera follows entrant 0. Both of those are
already true and neither needs a flag - so a viewer does not need a car of their
own at all:

**Rotate the driver being watched into entrant 0.** `RaceGrid.Order` already
rotates this machine's own player to the front, because the human always drives
entrant 0. A viewer has no player to rotate, so it rotates the driver it is
watching instead. The camera then follows that driver, the six cars are the same
six cars, and every one of them is moved by the poses already on the wire - the
steering the pad still does is overwritten by the watched driver's own pose the
same way a remote car's is.

Zero new mechanism. What it does not give is switching mid-race: who is entrant
0 is decided when the race is built, because the car models are loaded per
entrant. Switching while the race runs needs the camera's own target.

**The player asked for mid-race switching**, so the target has to be found.

### Hunting the camera's target

Four static searches and two instrumented runs, and it is still not found. What
each ruled out is worth keeping, because the next attempt should not repeat
them.

Static, all empty:

- The car array base (0x800A9688) is taken in ten places in `gt2_01`. The ones
  that are not the physics use **car 0 by constant address**, not by index.
- The three functions outside the physics that read a car's place
  (0x8004E88C and neighbours) are text: they call 0x8006C460 with format
  strings out of 0x8005B3xx.
- The GTE control loads are libgte's own helpers in `main.cs`; the caller is
  what matters and static reading did not reach it.
- Shoulder buttons near the race context: three sites, none a camera change.

A read watch on car 0's place (0x800A9D10), armed at the race's first frame,
spent its whole budget on setup and returned `entry_80012CD4` - the function
that builds a car slot from an entrant, taking the entrant index in A1. Useful,
and not the camera. That is what `GT2_WATCH_READ_AT` and `GT2_WATCH_READ_HITS`
were added for.

Armed 300 frames in, the same watch returned seven per-frame readers, **all of
them under `entry_8003EBF0`** - the pass that walks every car:

```
func_80033E6C <- func_80034320 <- func_80034480 <- entry_8003EBF0
entry_80041AE8 <- func_80033E6C <- ...
entry_80043388 <- entry_8003EBF0
entry_80043AE0 <- entry_8003E8E4 (the physics stepper)
entry_8003E7EC <- entry_8003E8E4
entry_8003CE3C <- entry_8003CF94
entry_8001336C <- entry_800133F0   (already named: a car's GTE matrices)
```

`func_80034480(A0 = car array base, A1 = car count)` loops all cars and then
calls a run of `f(base, count)` passes, so that chain is per-car too. Every one
of the seven is per-car, which is evidence that **the view does not read a car's
place to decide what to follow** - or does not read the place at all.

A car does carry camera-shaped fields - `func_80032B0C` builds rotation rows at
car +0x668/+0x670/+0x678 out of three angles at +0x644/+0x646/+0x648, through
`gt2_ovr1_race_rotation_matrix_from_three_angles`, the same function a car's own
rotation goes through. But +0x644 is also read by the physics stepper, so those
are the car's fields rather than a camera's.

### Asking which code consumes the pad

A read watch on the race's own pad reader record at 0x800A9528, armed 300 frames
in, came back with thirty-eight distinct stacks of which **exactly two** are
consumers - every other one is the decoder filling the record from the VBlank:

```
entry_80014BB4                                    reads +0x02
gt2_ovr1_race_screen_step_one_frame_...           reads +0x03
```

`entry_80014BB4(A0 = the pad reader record)` is now
`gt2_ovr1_race_read_one_pad_according_to_its_controller_type`. The byte at
+0x02 is not a button: it is compared against 2, 5, 6, 7 and 0x0E, which are
controller types - digital, analogue, dual shock, wheel. The function then
copies +0x48..+0x58 to +0x78..+0x88, this frame's input over last frame's.

So the record is about 0xB0 bytes - pad 1's sits exactly 0xB0 further on - and
the twenty-byte span only covered its header. **Steering reads +0x48 and up,
which is why it never appeared.** A wider span would name it, and that is the
answer `SecondDriver` has been waiting for.

It also gives a viewer a clean way not to drive: neutralise the record this one
function reads, rather than hunting for a flag on an entrant.

### Why the camera target has not turned up

Because a GT2 race very likely has no such thing. In a race the player cycles
the camera's **position** - chase, bonnet - and never its **subject**. Following
a different car is a replay control, and the replay does not run standalone
(see above). Everything found agrees: the view is tied to the player's car
structurally, not through a variable that names which car it is.

So mid-race switching is not a variable to find. It has to be built, and there
is a way to build it out of pieces that already work:

- the **pose** of the driver being watched goes into car slot 0 every frame,
  which is what the camera follows and what `RemoteCars` already knows how to
  write;
- on a switch, slot 0's **body** is changed to the watched driver's car, by
  calling the game's own `load_car_parts` (0x80076FC0) the way the arcade's
  builder calls it - `DirectRace.Call` already knows how to call game code from
  a hook and put every register back.

That second half is unproven. It is a call into the game mid-race, which is
exactly the shape of thing this port has been bitten by before.

## What stands a car on a square

Not the entrant's index. The byte at **+0x8D** of an entrant, which this port
writes from the player's seat in the room.

Four machines racing at once, each reporting where the game had stood all six
cars before anything moved one:

| place | square (x, z) |
| --- | --- |
| 0 | (713339, 358892) |
| 1 | (695895, 314351) |
| 2 | (654697, 336622) |
| 3 | (636879, 291710) |

Every machine holds the same four drivers in its own rotation - each leads with
its own player, because the pad drives entrant 0 - and every machine reports the
same square for the same number. Slot 0 is a different square on each. So the
number decides and the slot does not.

That closes two things at once:

- **The correction is not needed.** Standing a machine's own car on the square
  its seat was holding was right while the grid was numbered by slot, and
  became wrong the moment the number came from the room: entrant 0 already
  carries its own seat's number, so moving it again takes it onto somebody
  else's square. Two players came out on one square again, and four did too.
- **The leftovers are still open, but narrower.** Numbering the spare entrants
  4 and 5 put every car on one square, which is not what a placer that simply
  reads a number would do. A place past the entrant count reaching past the end
  of the course's list of squares would explain it. Untested - the room's own
  four are numbered 0 to 3 and are correct.

## 5. The end of a race returns to the lobby

Not to the arcade menu. The room outlives the race, so a second race can be
started from the same room.

### Where the end of a race is

It is not a finish line, a results screen or a phase number. A race is an
overlay: the arcade loads gt2_01 over itself to run one, and when the race is
done gt2_01 is gone and something has to be loaded in its place. Every overlay
load goes through `gt2_load_overlay`, which this port already hooks - so **the
end of a race is the next overlay to arrive after the race's own**, whether the
race was won or quit.

Only the *first* arrival after the race counts. The arcade fetches several more
while it sets a race up, and reading one of those as another ending would run
the lobby again in the middle of starting a race.

### Coming back costs nothing to build

The room already outlives the race. The session socket is kept rather than
dropped when a lobby ends in a race, and every player is still in the Room
object they left, so returning is reopening the panel over it. There is no
overlay to redirect either: the arcade - which is what a race starts from - is
the overlay already arriving.

### What has to be undone

This port's own memory of the race just run. Every class that does something
once per race would say it had already happened, and each would be quietly
wrong rather than loudly broken:

- `RaceStartLine` - the barrier lives on frame zero. A second race that kept
  the first one's frame counter would never hold, and every machine would begin
  whenever it finished loading.
- `DirectRace` - three flags say a screen has already been wound down, and a
  second race walks through the same two arcade screens.
- `CarSync`, `ReplayView`, `RacePhases` - the grid report, the replay a viewer
  is owed, the phase the room holds at.
- The host's "come to the race", on each client. Left standing it takes them
  straight back out of the lobby they have just returned to.

## 6. The other cars behave visibly like cars

Acceleration, braking, wheel rotation and the rest of the visible effects
should look on the remote machines the way they look on the owner's. Place and
heading travel already; this is the rest of what is drawn.

### Where a car begins

`0x800A9688`, stepping by `0xB40` - the race context's own car array. Not
deduced: `gt2_ovr1_race_car_build_the_matrices_it_is_drawn_from` is hooked and
prints the pointer it is handed, and the six came back exactly there.

`RemoteCars.FirstCar` is `0x800A9B04`, which is **0x47C into a car** rather than
at its start. That cost nothing while everything this port wrote was addressed
from it, and cost two wrong answers the moment gt2_01's own offsets were used.

### Two guesses from reading, both wrong

Kept because the reasoning was sound and the measurement still says no.

- **`+0x5A` is not speed.** The matrix builder reads it into the vector
  `(0, 0, it)`, turns it by the car's matrix and adds the result to the drawn
  position, which is exactly what a speed would do. It reads 500 and stays there
  through acceleration, braking and a full stop. A fixed offset along the nose.
- **`+0x7CC + wheel * 0x10` is not the wheel's angle.** That is where
  `gt2_ovr1_race_car_set_its_four_wheels_draw_angles` writes three angles per
  wheel, at exactly that stride. The memory holds `(-3055, -5004)`,
  `(3055, -5004)`, `(-3063, 5024)`, `(3063, 5024)` and never moves: left and
  right, front and rear. Where the wheels are *mounted*.

### What actually moves

Measured instead of guessed - a copy of the car kept per frame, counting how
often each halfword changes over 1200 frames of driving:

| range | what it is | moved |
| --- | --- | --- |
| `+0x670` | the three heading angles | 926 |
| `+0x688` | position, X Z Y | 929 |
| `+0x694` | the rotation matrix built from the angles | 813 |
| `+0x6AC` | the second copy of both, 0x24 on | 929 |
| `+0x48C + wheel * 0x68` | a physics block per wheel, four of them | ~330 |
| `+0x7C6 + wheel * 0x10` | **one live angle per wheel** | ~880 |
| `+0x804`, `+0x81C`, `+0x830`, `+0x850` | the drawn matrix and position | ~930 |

The first four are what already travels. The last row is derived from them.

What is new is the middle two. `+0x49C` - the first wheel's physics block plus
0x10 - is the value the drawing code subtracts from a per-axle angle to get the
wheel's own, and the result lands in `+0x7C6 + wheel * 0x10`: four shorts,
holding four different values, moving on three frames in four. The other two
shorts of each drawn triple never move.

So the visible half of a wheel is one short, four times, and it is the thing to
send.

# Second backlog, agreed 2026-08-31

Four more, in the host's hands. Recorded in full before any of them is started,
and taken one at a time in this order.

## 7. The host chooses how many laps

One to ninety-nine, where today every race is two.

### Where the lap count is not

Not in any capture. Every race ever captured here - two arcade, two from the
attract demo, and the template - was run over two laps, so every candidate byte
reads 2 in all five. The header holds exactly four that do:

    +0x02   +0x05   +0x08   +0x0F

and the three bytes that separate an arcade race from a replay - +0x04, +0x09
and +0x0A - are already accounted for, as is the entrant count at +0x5A.

Reading gt2_01 does not settle it either. The record is addressed from dozens of
places and none of them reads a small header offset and counts with it.

### So the HUD was asked instead

`GT2_LAPS_PROBE` wrote a different number into each of the four candidates and
the lap counter named the one the game reads. It said **Lap 1/11**, so the lap
count is the byte at **+0x0F**. One race, one answer.

One byte, so ninety-nine laps fits with room to spare. The probe is kept behind
its switch: it is what would name the byte again on a build that moves it.

### The host's choice, from the lobby to the record

`Room` gained a `Laps` byte, defaulting to the two an arcade race is built as -
so a room that never chooses runs the race the game would have run anyway. It
travels in the room the host publishes (wire version 5), which makes it the same
kind of thing as the track: chosen once, read everywhere.

Clamped in three places, because the number arrives from three: a slider on the
host's machine, a socket on everyone else's, and `GT2_LAPS` on a run being
tested.

Two tests that located a byte by counting through the format broke when the laps
byte landed between the player limit and the player count. Both now find their
byte by serialising two rooms that differ in one field and asking which byte
differs, which cannot go stale.

## 8. The finishing order is shown on the way back to the lobby

Who came where, and what each driver's race time was. The return to the lobby is
already the end of a race (see item 5), so this is what to show on the way
through it. Both numbers come from the game rather than being timed here.

### How far a car has got: +0x634

A short in the car, counting the laps that car has completed. Found by asking
for the opposite of what the wheel hunt asked for - the fields that move a
*handful* of times in a whole race rather than every frame - and confirmed
across two races that differed: it read 3 after three laps and 2 after two, and
never fell.

Its neighbour `+0x630` is a word that goes with it: the code at 0x80010CC8 reads
both and passes them together to 0x800117C4, which is how far round the lap the
car is.

### How long it has taken: still open

Three passes have not found it, and each failure narrowed the question.

- **Not a field that always rises.** Counting rises and falls over *halfwords*
  finds no clock at all, because a 32-bit counter's low half wraps every 65536
  and that reads as a fall. Counting words instead found two, and neither is the
  race time: `0x800A8D72` counts frames exactly, and `0x800A8C64` counts two per
  frame. Both were already running before the lights went green.
- **Which is also why a rising-field search cannot find it.** A clock that is
  reset at the green light falls once, and a search for what never falls throws
  away the one field it is looking for.
- **Not in the race's own 64 kilobytes.** A race that ended at 2:24.009 held
  144009 nowhere in 0x800A0000..0x800B0000 - nor 8640 sixtieths, 10800
  seventy-fifths, 14400 hundredths or 4320 thirtieths - and no offset in a car
  held six values that could be six finishing times.

So the whole of RAM was kept instead, at the moment the race overlay is
replaced - the last instant the race's own memory is still standing - and
searched for the number the screen had shown.

### How long it has taken: milliseconds, in one of three places

A race that ended at **2:21.456** held **141456** exactly, as a 32-bit word, in
three places. So the unit is milliseconds and the only question left is which
address. They are not equally believable:

| address | what surrounds it |
| --- | --- |
| `0x801D5F80` | 0x198 past the end of the race record at 0x801D585C, among five-word entries on a 0x14 stride reading `-1 -1 -1 -1 -65536` - which is what an unset time looks like |
| `0x8005AC80` | inside a repeating 0x20-byte structure of large constants |
| `0x801B75D4` | surrounded by values in the hundreds of millions |

The first looks like a race result and the other two look like data that happens
to contain the number. That is an argument, not a measurement, so all three are
read and printed at the end of every race beside the lap count. The next race to
be run for any reason names the real one: a two-lap race that took 2:21.456 says
2 laps and 2:21.456, and whichever address disagrees is not the time.

Lap times were looked for too, as a corroboration, and are not stored as
adjacent millisecond words: no pair or triple anywhere in RAM sums to the total.

`0x801D5F80` is taken as the time, on the argument above rather than on proof.
`GT2_RACE_TIME_AT` moves it, so if a race shows one of the other two tracking
the screen instead, saying so costs a run rather than a build.

### What the end-of-race snapshot settled

The two-megabyte snapshot was taken to find the race time, and answered three
more questions at no cost - the last of them without running the game again.

- **The lap counter is cleared by the teardown.** All six cars read 0 at the
  moment the overlay is replaced, so a race that had plainly been driven
  reported "0 laps". It is watched every frame now, highest reading winning,
  since the last frame before the end may already be the one that cleared it.
- **And it is the lap a car is *on*, from one - not laps completed.** Read as
  completed, it was one too many everywhere, and two readings of the same race
  caught it: a one-minute race called its last lap while the car was still on
  lap one and the screen then read "Lap 1/2", and the standings claimed two laps
  where the game's own results screen listed one. Laps finished is this less
  one, never below zero.
- **`+0x0F` really is the lap count.** The record read 1 for a race the room
  had set to one lap - a second, independent confirmation of what the HUD probe
  named.
- **`+0x8D` does not become the finishing order.** The entrants still read the
  grid this port wrote plus the numbers the arcade left in the spare ones. It is
  the starting place and stays the starting place, so the order a race finished
  in has to come from comparing laps and times, which is what this does.

`+0x630` is per-car and belongs to the race: it held the same large value for
the two cars that raced and zero for the four that did not.

### Everyone times their own race

Nobody can time anybody else's. Every other car on a screen was teleported there
frame by frame, so when it appeared to cross the line is a fact about the network
rather than about the race - which makes each driver's own machine the only one
that raced them.

So each machine reads its own driver's laps and time at the moment the race
overlay is replaced, and reports them keyed by the room's seat, under a wire
code of their own. The *first* report from a seat wins - the opposite of a
place, which is a snapshot where only the latest is worth having. A result is
final the moment it is sent, and is repeated only in case a datagram was lost.

### Machines are not together in time

The first attempt collected for three seconds at the end of a race, and
recorded nothing but the machine's own result. Two screens from the same race
read **1:35.970** and **3:43.780**: the windows did not overlap at all, and
while a machine is still racing it is not listening for results anyway.

So the exchange lives in the lobby loop instead. A machine that is back keeps
saying how its race went, every quarter second for five minutes or until every
driver has reported, and keeps listening while the next race is being arranged.
Nobody has to arrive anywhere at the same moment.

### The room has to survive the race it exists to start

Silence times a machine out after three seconds - and nobody sends lobby
traffic while the game is running. So the room took itself apart every time it
was used: the client lost it to "The host left the room", the host pruned every
player who was out on track, and whoever got back first found a room list
instead of a room and had to join again by hand.

The fix is not a longer timeout. A race has no length worth guessing at, since
ninety-nine laps is allowed, so the room is *held together* from the moment the
lobby ends in a race and let go when every driver has said how their race went -
the first moment silence means something again. Letting go forgives the silence
it was holding through, or the very next tick would drop the whole room for
having been quiet all race. A driver who never reports is what the result
patience is for: when it runs out, the room stops waiting and drops them.

Nobody has to come back in any particular order, and nobody rejoins anything.

### One more thing had to change

One more thing had to change for the results exchange to work at all. The lobby's own two
receive loops drain the same socket and discard whatever they do not recognise,
so a result that arrived on their turn rather than on the collector's was simply
eaten - and which turn it lands on is a coin toss sixty times a second. All
three loops now keep a result.

### The order, and who is not in it

Most laps first, then least time. Laps have to come first: a car a lap down can
be quicker over the distance it covered, and sorting on time alone would put it
ahead of the car that beat it. It is also the rule a timed race will need.

A driver nobody heard from goes last, keeping the room's order among their own
kind, and is shown with no time rather than a guessed one.

The standings are not part of the room and the host does not publish them: every
machine works out the same list from the same reports. They are shown in the
lobby itself, above the room about to run another race, because the lobby is
where a race ends now - a results screen the player had to dismiss would be a
door in the way of the thing they came back for. Leaving the room forgets them.

## 9. A race by laps or by time

The host picks which. A timed race runs from one minute to three hours, chosen
on a slider - five was the shortest asked for, and one is there because the
ending has to be watched to be believed and watching it five minutes at a time
costs five minutes at a time. When the time runs out the cars finish the lap they are on, and the
race ends there: most laps in the least time wins.

### Nothing here ends a race

The game already knows how to end one - it does it when a car completes the lap
the record's `+0x0F` names - and *finish the lap you are on* is exactly what
that does. So a timed race is a lap race whose lap count is not decided until
the clock runs out:

- it starts at 99, the most the byte will hold, so the game has no reason to end
  it early;
- when the time is up, `+0x0F` becomes the lap this car is on plus one, and the
  game ends the race when that lap is completed.

The ending is then the game's own, with its own results screen and its own
timing. Driving the race loop into its finished state from outside would have to
reproduce all of that, and would leave two different ways for a race to end.

**What one timed race has to confirm:** whether the game reads `+0x0F` again
once a race is running, or copies it somewhere at setup. If it copies, the lap
count has to be found in its copy instead - and the log prints what was written
and what reads back, so the run says which.

### Whose clock

The game's, not a stopwatch here - the same rule as the lap count and the race
time. Which of the game's two is unsettled:

- `0x801D5F80` holds the race time in milliseconds and matched the screen
  exactly at the end of a race, but whether it ticks *during* one has never been
  watched;
- `0x800A8C64` rises by exactly two a frame, and the screen's clock matches
  frames times two over sixty.

So the deadline is measured against the counter that is known to be running, and
both are printed when the last lap is called. One timed race settles which to
keep.

### How long is left, in the corner

A race against a clock is unplayable without it. A lap race tells the driver
where they are on every frame - Lap 2/5 - and a timed race otherwise says
nothing at all until it suddenly ends.

Drawn by the host in the bottom-left of the window rather than into the game's
own HUD. The game has no idea this race is timed, so there is nothing of its to
add a field to, and putting one there would mean working out how it lays a HUD
out. The corner of the window is already the port's.

It turns red under thirty seconds, and when the clock runs out it says LAST LAP
rather than 0:00 - by then the race ends when the lap does, and a countdown
sitting at zero would be saying the wrong thing rather than nothing.

The number itself is worked out where the game's memory is at hand, once a
frame, and read where it is drawn - the thing that draws is handed no memory.

### Laps or a clock, one control, and it lives in the room

They are one decision - a race is run to one or the other and never to both - so
there is one radio pair and one slider, not two sliders inviting somebody to set
the one being ignored.

It is chosen **when the room is made**, beside the track, the class and the
player limit, and not in the lobby. The lobby is where competitors sort
themselves out and choose cars; a control there that changes what everybody is
about to race does not belong among those. So the room is opened already
decided, rather than opened with a default and corrected afterwards.

The lobby and the room list both show what the room is - `2 laps` or `15 min` -
as text beside the track. Reading is not choosing, and somebody picking a room
from the list should know which kind of race it runs before they join it.

Zero minutes means laps, which keeps a room that never heard of any of this
running exactly as it did. It is also the one value not clamped on the way in
off the wire: zero is not a too-short race.

The standings needed nothing. Most laps, then least time, was already the rule -
written for a lap race that ends with cars on different laps, which is every
timed race.

## 10. The host arranges the grid

Today the grid is filled in the order players joined the room. Instead the host
arranges it, and what the host has not arranged keeps a default order. After a
race, the grid opens arranged by the finishing order of the race just run -
which the host can still change.

### The room's order is the grid

There is no second list of grid positions, and there should not be. RaceGrid
already numbers each entrant from where its player sits in the room, the wire
already keys everything by that seat, and the byte the game stands a car by
(+0x8D) is written from it. A separate list saying the same thing in a
different order would be one more thing able to disagree with the room.

So arranging the grid is rearranging the room, and it is the host's to do - the
host publishes the room, everyone else reads it, and nothing new goes on the
wire. Seats move when it happens, which is safe in the lobby and nowhere else:
by the time a race is built every machine holds the same room.

Viewers are not on the grid and are not moved. They keep the end of the list,
which is where `Seats` already expects them.

### Three rules, and why each is the way it is

- **A move off either end does nothing** rather than wrapping. Somebody
  clicking "up" at the front of the grid never means "put me last".
- **A newcomer joins at the back**, which falls out of the room appending
  rather than being arranged for.
- **After a race the grid opens in the finishing order**, winner at the front,
  applied once when the last result arrives rather than as each one lands -
  otherwise the grid shuffles under a host who is reading it. A driver the
  standings do not name keeps their place behind those who are named, rather
  than being dropped or given a result they did not earn.

The default when the host arranges nothing is the order people joined, which is
what the room already was.

# Qualifying, agreed 2026-09-01

The host may have the room run a qualifying session before the race. Off by
default. Two laps, always. The lobby waits for the host to start it, comes back
afterwards, and waits again for the host to start the race. Each driver's best
lap is shown, and the grid is ordered from the quickest to the slowest.

## Almost none of it is new

A qualifying session *is* a race - the same launch, the same start barrier, the
same return to the lobby, the same exchange of results - differing in three
things:

- it is always two laps, whatever the room races over;
- it is scored on the best lap rather than on distance and time;
- when it ends, the room moves on to arranging the race.

## One value, not two flags

The room carries a **stage**: qualifying, or racing. Not a "has qualifying" flag
beside a "has qualified" flag, because two would allow a state that means
nothing - qualified without qualifying - and every machine has to agree on what
the Start button is about to do. It travels with the room like the track and the
length do (wire version 7), and it only ever moves one way: the grid qualifying
produced is what the race is about to use, so a room falling back would be
throwing that away.

Two laps rather than the room's own length is a rule and not a default. A room's
lap count and clock are what the *race* is; qualifying borrowing them would let
a three-hour qualifying session be asked for by accident.

## Where a best lap comes from

Measured between two turns of the lap counter, on the counter at `0x800A8C64`
that rises by exactly two a frame.

It has to be that one. `0x801D5F80` holds the race time and matched the results
screen exactly - but a lap turning mid-race reported it as `--:--.---`, so it is
written when a race ends and reads zero throughout. That settles a question
these notes had left open: the millisecond clock cannot be read while a race is
running, and the sixtieths counter can.

So a lap time here is exact to a sixtieth of a second where the game's own is
finer. Every machine measures the same way from the same counter, which is what
qualifying on it requires.

## Scored on the best lap alone

A race asks who got furthest quickest, so laps come first and time breaks the
tie. Qualifying asks nothing about distance: everybody runs the same two laps
and only the best counts, so a driver who spun on one lap and was quickest on
the other qualifies on the quick one. A driver who never finished a lap has no
time to qualify on and goes last, rather than first with a zero.

## Asking the race a question after it has gone

The first run of this scored every qualifying session as a race: the table came
out titled "Last race" and sorted by distance and time. The rule was right and
the question was asked in the wrong place - "was this a qualifying session?" was
put to `DirectRace.Racing` from the lobby, and by the time the lobby scores
anything the race has been forgotten, so the answer was always no.

It is read now while the race is still standing, at the same moment the laps and
the time are read off its memory, and kept beside them. Everything about a
session that is not on the wire has to be taken then: that moment is the last
one where the race exists.

## The lobby says which it is

The same room is a qualifying lobby and then a race lobby, so it carries a title
saying which, the Start button reads "Start qualifying" or "Start race", and the
results table shows what the session was decided on - best laps after
qualifying, laps and total time after a race.
