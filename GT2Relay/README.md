# gt2relay

A rendezvous and relay for GT2 rooms. It exists so that neither the host nor
the players have to forward a port: both reach this with outbound UDP, which
every home router allows, and it passes their traffic along.

It understands nothing about GT2. A room's description is a blob it stores and
hands back; a room's traffic is a payload it copies from one datagram to
another. The game's protocol can change without this being redeployed.

It also holds no state worth keeping. Rooms live in memory and expire after
thirty seconds of silence, so there is nothing to back up and a restart costs
the lobbies two seconds to rebuild themselves.

Everything below was walked through on a real Oracle Cloud instance. Each trap
it names is one that actually happened, in the order it happened.

## Building it

**Match the architecture to the machine you are deploying to, not to the one
you are building on.** Oracle's Always Free tier offers both an ARM shape
(`VM.Standard.A1.Flex`, up to 4 OCPU and 24 GB) and an AMD micro, and the RID
has to follow whichever you took. `uname -m` on the target settles it:
`x86_64` means `linux-x64`, `aarch64` means `linux-arm64`. Getting this wrong
produces a binary systemd refuses with `203/EXEC`, and running it by hand says
`cannot execute binary file`.

```bash
dotnet publish GT2Relay -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o out
```

One file, about 39 MB on x64 and 37 MB on arm64, with the .NET runtime inside
it. Nothing to install on the server. Without
`EnableCompressionInSingleFile` the same build is 74 MB; the compression costs
a few milliseconds at the first start and nothing afterwards.

The smaller alternative needs the .NET 10 runtime installed and kept up to
date on the server, which for a process you want to forget about is a worse
trade:

```bash
dotnet publish GT2Relay -c Release -r linux-x64 --self-contained false -o out
```

## Putting it on the machine

```bash
scp -i your.key out/gt2relay opc@YOUR.IP:/tmp/gt2relay
```

Then, on the instance — **`cp`, not `mv`**:

```bash
sudo mkdir -p /opt/gt2relay && sudo cp /tmp/gt2relay /opt/gt2relay/gt2relay && sudo chmod +x /opt/gt2relay/gt2relay && sudo restorecon -Rv /opt/gt2relay
```

Oracle Linux runs SELinux enforcing, and `mv` preserves the label a file
carried in your home directory. A binary still marked `user_home_t` is one
systemd is not allowed to execute, and it fails with `203/EXEC` — the same
code as the wrong architecture, which makes the two easy to confuse. `cp`
creates a new file that inherits the destination's label; `restorecon` fixes
one that arrived the other way. `ls -Z` shows which you have, and
`sudo ausearch -m avc -ts recent` says outright when SELinux is the one
refusing.

### As a service

`/etc/systemd/system/gt2relay.service`:

```ini
[Unit]
Description=GT2 rendezvous and relay
After=network-online.target

[Service]
ExecStart=/opt/gt2relay/gt2relay --port 34720
Restart=always
RestartSec=2
DynamicUser=yes
NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=strict
ProtectHome=yes

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now gt2relay
```

```bash
sudo journalctl -u gt2relay -n 20 --no-pager
```

It says `gt2relay listening on udp/34720` when it is genuinely up. Do not take
`systemctl status` saying *active (running)* as proof on its own: a .NET
process that throws on startup takes a few seconds to write a core dump, and
`status` run inside that window shows it running. The listening line is the
one that means something.

## Making it reachable

### The instance needs a path to the internet

Its subnet must be **public**, the VCN must have an **internet gateway** with a
`0.0.0.0/0` route to it, and the instance must have a public IPv4 address. The
*VCN with Internet Connectivity* wizard builds all three; a VCN assembled by
hand may have none of them.

**Reserve the public IP** rather than keeping the ephemeral one. An ephemeral
address changes when the instance is stopped, and the address is what players
type into their Server box and what a shipped `config/relay.txt` points at.

### Two firewalls, and both block UDP by default

Opening one and not the other looks exactly like the server being down.

**1. The cloud side.** *Networking → Virtual Cloud Networks → your VCN →
Security Lists → Default Security List → Add Ingress Rules*:

| Field | Value |
| --- | --- |
| Source Type | CIDR |
| Source CIDR | `0.0.0.0/0` |
| IP Protocol | **UDP** |
| Source Port Range | **leave empty (All)** |
| Destination Port Range | `34720` |

Two ways to get this wrong, both of which produce a completely silent failure:
choosing **TCP**, which nothing here speaks, and filling in the *source* port
range with 34720. A player's source port is whatever their machine happened to
allocate, so pinning it rejects nearly everyone.

If the instance also has a network security group attached, either place works
— OCI takes the union of security lists and NSGs, so one rule permitting the
traffic is enough.

**2. The machine side.** Which tool depends on the image, and guessing wrongly
wastes an evening:

```bash
systemctl is-active firewalld nftables iptables
```

Oracle Linux answers `active` for **firewalld**, and the rule persists by
itself:

```bash
sudo firewall-cmd --permanent --add-port=34720/udp && sudo firewall-cmd --reload
```

Ubuntu images ship a populated `iptables` that `ufw` does not front. There the
rule has to be **inserted** rather than appended, because the REJECT rule
already in the chain would otherwise match first — and `netfilter-persistent`,
which exists only on Debian and Ubuntu, is what makes it survive a reboot:

```bash
sudo iptables -I INPUT -p udp --dport 34720 -j ACCEPT
```

```bash
sudo apt install -y iptables-persistent && sudo netfilter-persistent save
```

## Pointing the game at it

Three ways, in the order they win: `GT2_RELAY` in the environment beats
everything; an address typed into the lobby's **Server** box is remembered per
machine and beats the shipped default; and the shipped default is the first
useful line of `config/relay.txt` beside the executable, copied from
`config/relay.txt.example`.

A value already saved from the Server box outranks a later edit to
`relay.txt`, so on a machine that has been pointed somewhere before, editing
the file changes nothing — retype the address and press **Use**.

One thing worth knowing before you conclude the relay is broken: **a room
created with a Secret is published but not listed.** It is reachable by its
six-character code and never appears under *Rooms on the internet*, on
purpose, because listing a private room advertises its existence to exactly
the people who cannot get in. Test with the Secret box empty.

## Checking it

The service's own log is the fastest instrument in the box. It prints a line
whenever the counters move, so a quiet night stays quiet and a scrolling log
means traffic:

```bash
sudo journalctl -u gt2relay -f
```

```
in 31  out 25  failed 0  rooms 1  last 177.139.69.38:54088
```

| What you see | What it means |
| --- | --- |
| `rooms 1`, `out` rising | Working. That room is registered here and answers are going back. |
| `in` rising, `rooms 0` | Datagrams arrive but none is a publish — the host is pointed somewhere else, or is an older build. |
| `in` rising, `out` flat | The registry has nothing to answer with. A list with no *listed* room is silence by design; see the Secret note above. |
| `failed` rising | Sends are being refused — the peer went away, or the outbound path is blocked. |
| No lines at all | Nothing is being read. Check the service is actually up, then the two firewalls. |
| Restart counter climbing | It is crash-looping. The stack trace is in the same journal. |

From outside, on the wire:

```bash
sudo tcpdump -n -i any udp port 34720
```

Arrivals show as `In` and answers as `Out`. **`In` with no `Out` is not proof
that the return path is blocked** — that was the first guess here and it was
wrong. A process that dies before reading, or one whose registry has nothing
to say, looks identical from the outside. Read the journal before blaming the
network.

And to confirm the socket is really held:

```bash
sudo ss -lunp | grep 34720
```

`0.0.0.0:34720` beside a `gt2relay` process is what you want.

## Elsewhere

[DEPLOY-GCP.md](DEPLOY-GCP.md) covers Google Cloud — one firewall instead of
two, but a much meaner free traffic allowance and a free tier that only exists
in the United States, which matters for latency.

[DEPLOY-TUNNEL.md](DEPLOY-TUNNEL.md) covers running this, or the game itself,
behind a free UDP tunnel: for carrier-grade NAT, where there is no public
address to forward and no cloud account is wanted either.

## What it costs

A relayed place is 33 bytes of game payload wrapped in 27 bytes of envelope,
which is 88 bytes on the wire once IP and UDP headers are counted, and each
player sends one per frame. A full six-player room is on the order of 50 to
100 KB/s each way, depending on how many of those players are reaching each
other through here rather than directly. Oracle's Always Free allowance is
10 TB of egress a month — thousands of room-hours either way.

## What it will not do

It has no accounts and no authentication: anybody who can reach it can make a
room. The caps in `RoomRegistry` — 256 rooms, 8 members each, 1 KB of card,
1 KB of payload — are what stand between that and a problem. A room that wants
to be private carries a secret, which its *host* checks; this only carries
bytes.

It will not forward for a stranger. Both ends of a relayed datagram have to be
members of the room it names, or nothing is sent — without that this would be
a machine on the internet that sends traffic wherever anybody points it.
