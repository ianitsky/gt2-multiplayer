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

## Running it

```bash
dotnet publish GT2Relay -c Release -r linux-x64 --self-contained false -o out
./out/gt2relay --port 34720
```

## On Oracle Cloud free tier

Pick the **`VM.Standard.A1.Flex`** shape — up to 4 OCPU and 24 GB are Always
Free, against 1 OCPU and 1 GB on the AMD micro. Reserve a public IP rather than
keeping the ephemeral one: an ephemeral address changes when the instance is
stopped, and the address is what players type.

**There are two firewalls and both block UDP by default.** Opening one and not
the other looks exactly like the server being down.

1. In the console: *Networking → Virtual Cloud Networks → your VCN → Security
   Lists → Default Security List → Add Ingress Rule*. Source `0.0.0.0/0`,
   IP Protocol **UDP**, destination port range `34720`.

2. On the instance, where Oracle's images ship a populated `iptables` that
   `ufw` does not front:

```bash
sudo iptables -I INPUT -p udp --dport 34720 -j ACCEPT
sudo netfilter-persistent save
```

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
sudo systemctl status gt2relay
```

## Checking it from somewhere else

```bash
sudo tcpdump -n -i any udp port 34720
```

Traffic arriving here but nothing going back means the outbound path is
blocked; nothing arriving at all means one of the two firewalls above.

## What it costs

Six players at 60 datagrams a second, ~29 bytes of payload each, is about
100 KB/s per room in and the same out. Oracle's Always Free allowance is 10 TB
of egress a month, which is thousands of room-hours.

## What it will not do

It has no accounts and no authentication: anybody who can reach it can make a
room. The caps in `RoomRegistry` — 256 rooms, 8 members each, 1 KB of card,
1 KB of payload — are what stand between that and a problem. A room that wants
to be private carries a secret, which its *host* checks; this only carries
bytes.

It will not forward for a stranger. Both ends of a relayed datagram have to be
members of the room it names, or nothing is sent — without that this would be
a machine on the internet that sends traffic wherever anybody points it.
