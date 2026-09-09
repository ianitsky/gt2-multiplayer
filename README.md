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
git clone --recursive https://github.com/ianitsky/gt2-multiplayer.git
```

Only `RecompOne` is needed to build. `externals/gt2-reversing` is reversing
notes this port drew on, and it carries submodules of its own that a Windows
checkout can refuse for path length; if the recursive clone stops there, this
is enough:

```bash
git clone https://github.com/ianitsky/gt2-multiplayer.git
```

```bash
git -C gt2-multiplayer submodule update --init RecompOne
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

Pick the second item on the title menu — the port renames it **MULTIPLAYER** —
and the multiplayer window opens over the game. Type a name, and either join a
room somebody is showing or press **Create a room**, choose a track and a
player limit, and wait for people in the lobby.

One machine hosts. The race itself is host and players talking directly to
each other, so the only real question is how the others' datagrams reach the
host's machine. There are four answers, and they can all be true at once: a
host reachable directly answers directly, and answers through a relay at the
same time.

Everything is UDP:

| Port | Who listens | What for |
| --- | --- | --- |
| 34718 | every player | rooms announcing themselves on the local network |
| 34719 | the host | the lobby and the race |
| 34720 | the relay | rooms and traffic passed between networks |

### 1. On the same network

Nothing to set up. The host presses **Create a room**; the room announces
itself on the network every second, and on the other machines it appears under
**Rooms on this network** with a **Join** beside it.

If it does not appear, the announcement is being dropped rather than lost:
some routers do not pass broadcast between wireless and wired, and a "public
network" profile on Windows blocks the port. Allow the game through the
Windows firewall on UDP 34718 and 34719, and check both machines are on the
same subnet.

### 2. Over the internet, with the game itself as the server

One port forwarded on one router, and no server to run.

The host's game asks the router itself: while it is hosting it tries UPnP, and
the lobby says which of these happened.

- *"Asking your router to open the port..."* — it is trying.
- *"Others join at 203.0.113.9:34719"* — done, and that is the address to hand
  out.
- *"The router would not open it: ..."* — UPnP is off or not supported. Forward
  it by hand: in the router's admin pages, forward **UDP 34719** to the host
  machine's local address. The wording varies — "port forwarding", "virtual
  server", "NAT rules".

Then allow the game through the host's own firewall on UDP 34719, and tell the
other players the host's public address. They type it into **Address** as
`address:34719` — the port is not optional there — and press **Join by
address**. If the room was created with a **Secret**, it goes in the Secret box
beside it.

A host with a home address that changes is worth pointing a dynamic-DNS name
at: the Address box takes a name as happily as a number.

This does not work behind carrier-grade NAT, where the public address is not
yours to forward. If the router has no WAN address of its own — it starts with
`100.64.` through `100.127.` — skip to 3 or 4.

### 3. With a relay

Nobody forwards anything. Both sides reach the relay with outbound UDP, which
every home router allows, and it passes their traffic along. It is also what
makes rooms visible outside a network at all.

Run it on any machine with a reachable address — a small VPS is plenty; it
holds no state and forgets a room thirty seconds after it goes quiet:

```bash
dotnet publish GT2Relay -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o out
```

```bash
./out/gt2relay --port 34720
```

One file with the runtime inside it, so there is nothing to install on the
server. Use `-r linux-arm64` if that machine is ARM — `uname -m` on it
decides, and a mismatch fails in a way that reads like a permissions problem.

Open **UDP 34720** in that machine's firewall — on a cloud provider that
usually means two firewalls, the provider's and the instance's, and opening
one and not the other looks exactly like the server being down.
`GT2Relay/README.md` covers Oracle's free tier and running it as a service,
and `GT2Relay/DEPLOY-GCP.md` covers Google Cloud.

Then point the game at it, in any of three ways:

- copy `config/relay.txt.example` to `config/relay.txt` and put the address in
  the copy, which is how a shipped build comes up already pointed somewhere;
- type it into the **Server** box, which is remembered for next time;
- set `GT2_RELAY`, which overrides both and is how a test machine points at a
  local server.

Rooms then appear under **Rooms on the internet**. A host is also given a
six-character code — *"Or by code K7QM2F through the relay"* — and anybody with
that code can type it into **Join by code** without knowing any address at all.
The alphabet leaves out the letters and digits people mistake for each other,
so a code survives being read out loud.

### 4. With a relay behind a tunnel

For when the machine running the relay cannot be reached from outside either:
carrier-grade NAT, a router you do not control, a provider that will not route
UDP to you.

[playit.gg](https://playit.gg) gives a free UDP tunnel. Run the relay on your
own machine as above, install the playit agent, and in its dashboard add a
tunnel with protocol **UDP** and local port **34720**. It answers with an
address like `something.playit.gg:41007`, and that address is what goes in
`config/relay.txt` or the Server box — the relay's own port is never typed by
anybody.

`GT2Relay/DEPLOY-TUNNEL.md` has the whole thing, including the variant that
tunnels the host's game instead of the relay, and the failure worth knowing
about: the tunnel agent authenticates against a clock, so a machine whose time
has drifted more than a few seconds gets a tunnel that dies quietly.

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
