# Playing through a UDP tunnel

For the connections nothing else reaches: behind carrier-grade NAT there is no
public address to forward, so the router has nothing to offer and neither UPnP
nor a port-forwarding rule can help. A tunnel is the way out. An agent on your
machine makes an *outbound* connection to a service, the service gives you a
public address, and traffic sent there comes back down the connection you
opened.

This is written against [playit.gg](https://playit.gg), which does UDP and is
aimed at exactly this. The shape is the same for any other UDP tunnel.

## Two ways to use it, and they are not equivalent

**Tunnel the game.** The host runs the agent on port 34719 and hands out the
address it is given. Nothing else runs anywhere. Only that person can host.

**Tunnel the relay.** The host runs `gt2relay` on 34720 and tunnels *that*.
The address goes in `config/relay.txt` so nobody types anything, and then
anybody can host — the relay is what has to be reachable, not the player.

Whichever machine runs the relay has to be on for anybody to see a room. That
is the real cost of keeping it at home rather than on a server.

Start with the first. It is one moving part instead of two, and it is enough
for two friends racing. Move to the second when somebody else wants to host.

---

## Tunnelling the game

### 1. The agent

Make an account at playit.gg, install the agent, sign in. On Windows it runs in
the tray; the account page is where tunnels are added.

### 2. A UDP tunnel to 34719

In the playit dashboard: add a tunnel, protocol **UDP**, local port **34719**.

It gives back something like `something.playit.gg:52341`. **The port will not
be 34719** — the service assigns it, and it is part of the address.

Leave the agent running. It must be up whenever you host.

### 3. Host, and hand out the address

Open the multiplayer panel, create the room as usual. The other player types
the whole thing into **Join by address**:

```
something.playit.gg:52341
```

**The port is not optional in that box.** A name on its own is refused.

If the room has a secret, they type it too. If it has none, anybody with the
address gets in — the same as it has always been.

### Why the port matters

The game used to keep only the address once the knock had gone out, and address
everything after it to 34719. Behind a tunnel that is a port nobody is
listening on, so joining appeared to work and then the lobby went silent. That
is fixed — the typed port travels with the session — but it is the failure to
expect if you are running an older build.

---

## Tunnelling the relay

Same agent, one more process.

### 1. Build and run the relay

```bash
dotnet publish GT2Relay -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true -o out
```

```bash
./out/gt2relay.exe --port 34720
```

It says:

```
gt2relay listening on udp/34720
rooms are forgotten after 30s without a publish
```

On Linux use `-r linux-x64`, and `README.md` has a systemd unit that keeps it
up across reboots.

### 2. A UDP tunnel to 34720

Second tunnel in the dashboard, protocol **UDP**, local port **34720**. It
gives back another `name:port`.

### 3. Point every player at it

Copy `config/relay.txt.example` to `config/relay.txt` and put the address in
the copy. That file ships beside the executable; the example is the one in the
repository, because an address is per-machine and a tunnel address is somebody's
own:

```
# The rendezvous and relay this copy of the game points at.
something.playit.gg:41007
```

Then nobody types anything — the **Server** box comes up already filled in and
the rooms are simply there. A player who wants a different relay types over it
and their choice is kept, including clearing it; `GT2_RELAY` overrides both.

A file rather than something compiled in, because the address is not
permanent: a free tunnel hands out a new one whenever it is recreated, and a
constant would mean rebuilding and redistributing the game to follow it.

Include the port. Leaving it off means 34720, and the tunnel's port is not
34720.

Then everybody sees everybody's rooms, and any of them can host without
touching a router.

---

## What this costs you

**Latency.** Traffic goes player → tunnel → your machine → tunnel → player.
Where playit's nearest node is decides how much that hurts; from Brazil expect
tens of milliseconds added, not hundreds. Worth measuring rather than assuming:
race once and see.

**A third party in the path.** Your game traffic goes through somebody else's
machines. It is a car's position and a lap time, not much to lose, but it is
worth knowing.

**An agent that has to be running.** If it is not up, the address is dead. Set
it to start with the machine.

## When it does not work

**The agent says connected, the game does not.** Check the tunnel's protocol is
**UDP** and not TCP. A TCP tunnel to 34719 connects happily in the dashboard
and carries nothing.

**Joining works, then the lobby is empty.** The typed port is being dropped -
an older build. `git log --oneline -1 -- patches/multiplayer/ModeHook.cs`
should show *Keep the port a host was typed at*.

**Nothing at all.** Windows Firewall still has to allow the game or the relay
to receive on its local port, even through a tunnel: the agent delivers to
`127.0.0.1`, but the socket has to be allowed to exist. Allow the app when
Windows asks, or add the rule by hand.

**Rooms are published but never listed.** Traffic goes in and nothing comes
back, which is the agent's receive loop being dead rather than anything about
the game. On Windows a UDP socket that sends to a port with nothing listening
is handed the resulting ICMP refusal as a `ConnectionReset` on its *next*
receive, and the playit agent does not turn that off the way this port's own
sockets do. The agent's log says so once per forwarded datagram:

```
WARN udp_receiver: failed to receive UDP packet error=Os { code: 10054, kind: ConnectionReset }
```

It does not recover on its own. **Start what listens before you start the
agent** - the relay on 34720, or the game if you are tunnelling the game -
and restart the agent if the order ever slipped. A tunnel left pointed at a
port nothing is bound to poisons the agent every few seconds.

The relay's own status line tells this apart from a dead tunnel without a
packet capture:

```
in 68  out 70  failed 0  rooms 2  last 192.168.15.7:58996
```

`in` climbing means the tunnel delivers. `in` climbing with `out` climbing,
while players still see no rooms, means the path back - the agent, not this.
`in` flat means nothing is arriving at all: the tunnel, or the firewall.

**The agent warns about the clock.** A machine whose time is off by more than
ten seconds has its agent rejected by the edge, and the tunnel goes quiet with
no other explanation:

```
WARN established_control: local timestamp if over 10 seconds off offset=111888
```

`offset` is milliseconds. Check with `w32tm /query /status`: a machine that has
never synchronised says *Local CMOS Clock* and drifts minutes a week.

**Check what the panel says.** While knocking it reports knocks sent against
datagrams heard, and the sending socket's own failures. Knocks rising with
nothing heard back means the tunnel, not the game.
