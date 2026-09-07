# GT2 RecompOne

Gran Turismo 2 (PlayStation, `SCUS-94488`) statically recompiled into C# with
[RecompOne](https://github.com/BlackLabelHQ/RecompOne), and given something the
original never had: racing against other people over the internet.

Static recompilation is not emulation. The game's MIPS code is translated into
C# ahead of time and compiled into a native .NET program, and the runtime
stands in for the parts of the PlayStation the code expects to find — the GPU,
the CD drive, the pads, the interrupt that ends a frame. What runs on your
machine is the game, as a program your machine understands.

**No game data is in this repository, and none ever will be.** Everything here
is code, symbol maps and notes. You supply your own disc.

## What works

- The game boots and plays: menus, licences, arcade, the simulation mode.
- Multiplayer, in alpha: a room list, a lobby, qualifying, and a race against
  other players. On a local network, and over the internet through a relay.
- Save data on emulated memory cards.

Rough edges worth knowing before you build it: a remote car is drawn
interpolated between the places that arrive for it, so a connection that drops
packets for a long stretch holds the car at its last known place rather than
guessing where it went. Loading is slower than it should be at one particular
moment of a race start, for a reason that is understood and written down.

## Getting it running

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and your own
copy of the game as a `.cue`/`.bin` or `.chd`. It has been built and played on
Windows.

Clone with the submodules — the recompiler is one of them:

```bash
git clone --recursive https://github.com/<you>/gt2-recompone.git
```

Put your disc image in `disc/` and point `config/gt2.json` at it:

```json
"cue": "../disc/Gran Turismo 2 Combined Disc (USA).cue",
```

Recompile the game into C#. This writes `generated/`, which is not in the
repository because it is derived from your disc:

```bash
dotnet run --project RecompOne/RecompOne.Recompiler -c Release -- config/gt2.json
```

Then run it:

```bash
dotnet run --project GT2Port.csproj -c Release
```

The first run writes a `settings.json` beside the executable. Point it at the
same disc image through the disc picker in the window, or edit `CdPath` there
by hand.

## Playing against other people

On one network, a host creates a room and the other machines find it under
"rooms on this network". Nothing else is needed.

Over the internet, both machines need a rendezvous server to find each other
through, and to relay the race when neither can be addressed directly. That
server is `GT2Relay/` in this repository — run it anywhere with a reachable UDP
port, or over a free tunnel. `GT2Relay/DEPLOY-TUNNEL.md` walks through the
tunnel, `GT2Relay/DEPLOY-GCP.md` through a machine with an address of its own.

Tell the game where that server is by copying `config/relay.txt.example` to
`config/relay.txt` and putting your address in the copy. A player can also type
one into the Server box, and `GT2_RELAY` overrides both. Without any of them
the game plays on a local network and the internet room list stays empty.

## Layout

| Path | What is in it |
| --- | --- |
| `config/gt2.json` | What to recompile, and every function worked out so far |
| `config/funcmaps/` | Symbol maps for the main executable and the overlays |
| `patches/` | Code added to the game: multiplayer, hooks, runtime helpers |
| `GT2Relay/` | The rendezvous and relay server |
| `GT2Port.Rendezvous/` | The client side of talking to it |
| `RecompOne/` | The recompiler and runtime (submodule) |
| `externals/gt2-reversing/` | Reversing notes this port draws on (submodule) |
| `tools/` | Python for disc extraction, symbol maps, cheats, images |
| `docs/superpowers/` | Design specs and implementation plans |
| `tests/` | The test suites |

## Tests

```bash
dotnet test tests/GT2Port.Tests/GT2Port.Tests.csproj
```

```bash
dotnet test tests/GT2Relay.Tests/GT2Relay.Tests.csproj
```

## Licence

The code in this repository is MIT — see [LICENSE](LICENSE). RecompOne, in
`RecompOne/`, is MIT and belongs to its own authors.

Gran Turismo 2 is not. It is Polyphony Digital's and Sony's, and nothing of it
is distributed here: no executable, no overlay, no track, no car, no sound.
This project reads a disc you already own. If you do not own one, this
repository will not get you a game.
