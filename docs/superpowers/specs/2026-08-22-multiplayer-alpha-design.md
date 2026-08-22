# Multiplayer alpha: mode hook, rooms and LAN lobby

**Date:** 2026-08-22
**Status:** Approved, not yet implemented

## Scope

Online multiplayer is not one feature. It decomposes into subsystems that each
deserve their own spec, plan and implementation cycle:

| subsystem | depends on | risk |
|---|---|---|
| Determinism (proving it, then keeping it) | nothing | highest |
| State save/restore | nothing | medium |
| Screens: room list, creation, lobby | nothing | low |
| LAN transport and discovery | nothing | medium |
| Start hook into multiplayer | screens | low |
| Rollback netcode in the race | all of the above | high |

**This spec covers only the alpha:** the mode hook, the three screens, and the
LAN lobby. It ends when every player in a room is ready and the host can start.

Starting the race, and everything about rollback, belong to later cycles.

### Deferred risk, recorded deliberately

Rollback requires the game to be deterministic: same state plus same input must
produce the same result, bit for bit. Whether the recompiled GT2 is
deterministic on this runtime is **unknown**, and known reasons to doubt it:

- the VBlank source is wall-clock, so two clients take interrupts at different
  points in the instruction stream
- safepoints deliver interrupts between instructions, and which one depends on
  timing
- there is no state save/restore at all — `CpuContext.Snapshot()` covers
  registers, nothing covers the 2MB of RAM, GTE, SPU or timers

The user chose to build the screens first, with this risk open. It is cheap to
test later: run two instances with identical input and compare a per-frame hash
of RAM. Recorded here so the choice is visible rather than forgotten.

## Constraints

From the user, and they shape every decision below:

- change the original game code **as little as possible** — the alpha changes none
- reuse as much of the original game as possible
- Start leads to multiplayer instead of Simulation mode
- the race itself will reuse the game's Arcade race path (next cycle)

## Architecture

The game is never modified. It runs normally until it asks for the Simulation
mode overlay, which is where control is taken — through the same `pre` hook on
`gt2_load_overlay` that already drives overlay activation.

```
gt2_main -> menu (gt2_02) -> player picks Simulation
                               |
               gt2_load_overlay(entry point of gt2_05)
                               |
         ModeHook recognises the Simulation entry point
                               |
    instead of loading: pause the game, hand control to the host
                               |
      [ room list -> create/join -> lobby ]   C# / ImGui
                               |
              on start: hand control back (next cycle)
```

Pausing is literal and cheap. The game only advances when a VBlank is
delivered, and the VBlank source is pluggable — the seam reserved when
interrupt delivery was designed. The host stops requesting frames, draws its
screens, and resumes when it chooses. The game is frozen and unaware.

**To confirm during implementation:** that `gt2_05` is the Simulation overlay.
Its symbols point that way (`buy_car`, `garage`, `get_car_price`), but this
must be verified by running rather than assumed, and the entry point mapped
from `patches/OverlayEntryPoints.cs`.

### Components

All new code lives outside the game. Nothing here edits generated output.

| component | responsibility |
|---|---|
| `Multiplayer.ModeHook` | recognises the Simulation entry point and diverts |
| `Multiplayer.Ui` | the three screens |
| `Multiplayer.Lan` | UDP discovery and the session socket |
| `Multiplayer.Session` | room state: players, car, track, who hosts |

Session state lives in C# from the start, because that is where the netcode
will need it — not in the game's own structures.

## Screens

Three screens, each a state of one machine:

```
[Room list] --create--> [Create] --> [Lobby] --all ready--> (race, next cycle)
     |                                  |
     '-------------join-----------------'
```

- **Room list** — rooms seen on the LAN: name, host, players (2/6), track.
  Refreshes on its own; rooms that stop announcing disappear.
- **Create** — room name, track, player limit. The creator becomes host.
- **Lobby** — connected players, each one's car, ready state. Only the host
  can start.

## Protocol

UDP, two channels with distinct jobs:

| channel | who speaks | contents |
|---|---|---|
| discovery | host to broadcast, once a second | room announcement: name, players, track |
| session | everyone to and from host | join, leave, pick car, ready, start |

The host is authoritative over room state and **retransmits it whole** on every
change — not deltas. With at most six players and a handful of fields the whole
state fits comfortably in one packet, and sending it whole removes an entire
class of desync bugs from a lost delta. Deltas would be premature optimisation.

No TCP: the same UDP socket carries the netcode later, and one network stack is
enough.

## Failure handling

Not generic error handling — the three things that will happen on a home LAN.

**Host disappears.** Clients stop receiving state; after 3 seconds without
news they return to the room list with a message. No host migration: that is
complexity the alpha does not need.

**Client disappears.** The host stops receiving its keep-alives, drops it from
the lobby after 3 seconds, and retransmits state. Others see the list shrink.

**Packet lost.** Because the host sends whole state on every change and
periodically, a loss corrects itself on the next transmission. This is what
makes whole-state worth more than deltas here.

**Duplicate or self announcements.** Every room carries a random id, and a
client ignores announcements from its own instance.

## Testing

What can be tested without a screen:

- **`Session`** — pure state machine: join, leave, ready, host leaves, timeout.
  No sockets, no UI. This is where logic that can be wrong actually lives.
- **Serialisation** — round-trip every message; a truncated or corrupt packet
  is rejected without throwing.
- **Discovery** — two sockets on loopback: one announces, the other finds it,
  then sees it expire.

The UI is not tested automatically. It is immediate-mode ImGui, and widget
tests would cost more than they are worth; its criterion is visual.

**Acceptance for the alpha**, and the thing that actually decides it is done:
two instances on different machines on the user's LAN — one creates a room, the
other finds and joins it, both appear in the lobby, both mark ready, and the
host's start becomes enabled.

## Out of scope

Stated explicitly, because this is where specs inflate:

- starting the race — the host's button ends this cycle by signalling "all ready"
- rollback, or any game-state synchronisation
- anything beyond the LAN: no server, no NAT traversal, no accounts
- validating determinism
