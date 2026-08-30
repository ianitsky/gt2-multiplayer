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

Pressing Start in the lobby shows the arcade's own screens for a few seconds
before the race loads. `DirectRace` already walks the arcade for us; it should
walk it without drawing it.

## 3. Car colour is chosen in the lobby

Every machine should draw every car in the colour its owner picked. The colour
belongs beside `Player.Car` in the room state, so it travels with the rest of
the room rather than over the race channel.

## 4. Viewers

A seat in the room that is not a car: the race is presented to them the way a
replay is.

## 5. The end of a race returns to the lobby

Not to the arcade menu. The room outlives the race, so a second race can be
started from the same room.

## 6. The other cars behave visibly like cars

Acceleration, braking, wheel rotation and the rest of the visible effects
should look on the remote machines the way they look on the owner's. Place and
heading travel already; this is the rest of what is drawn.
