# Putting gt2relay on Google Cloud

An alternative to `README.md`'s Oracle instructions, for when the Oracle free
tier will not have you. GCP is simpler to open up — there is one firewall
instead of two — but its free tier is much meaner about traffic, and where the
free machine is allowed to live decides whether the game feels good.

Read the two decisions first. They matter more than any of the commands.

---

## Decision 1: where the machine lives

Google's Always Free e2-micro is only free in **us-west1, us-central1 and
us-east1**. All three are in the United States.

Every datagram in a relayed race makes two hops: player → relay → player. Two
players in Brazil relayed through South Carolina turn a ~15 ms hop into roughly
**240 ms** of round trip, which in a racing game means the other cars visibly
lag behind where they are.

| Where the relay is | Round trip, two players in Brazil | Cost |
|---|---|---|
| `us-east1` (South Carolina) | ~240 ms | free tier |
| `southamerica-east1` (São Paulo) | ~30 ms | ~US$7/month for an e2-micro |

**If the players are in Brazil, put the relay in São Paulo.** The free tier is
the wrong saving here: it costs the thing the whole feature is for.

Two ways to avoid paying for it anyway:

- **The US$300 trial credit** covers 90 days. Long enough to find out whether
  anybody actually plays before spending anything.
- **UPnP first.** If the host's router can open the port (Task 0 of the plan),
  the relay is never in the path and none of this matters. The relay is for the
  households where it cannot.

## Decision 2: how much traffic it will carry

This is the free tier's real limit, and it is not the machine — it is
**1 GB of egress per month**.

A place packet is 29 bytes, wrapped by the relay to 56, and 84 on the wire. A
six-player race at ~30 frames a second moves roughly **75 KB/s out of the
relay**, which is about **270 MB per hour**.

| Racing per month | Egress | Free-tier bill (approx.) |
|---|---|---|
| 3 hours | ~0.8 GB | nothing |
| 10 hours | ~2.7 GB | ~US$0.20 |
| 50 hours | ~13 GB | ~US$1.50 |

Egress past the free gigabyte is around US$0.12/GB. Check current prices before
trusting the right-hand column; the left two are arithmetic and will not move.

**Set a budget alert anyway.** A relay with a stuck client is a relay that
sends all month, and a bill is a poor way to find out.

---

## The commands

Everything below uses `gcloud`. Each has an equivalent in the web console; the
CLI is here because it can be pasted.

Install the CLI, then:

```bash
gcloud auth login
```

### 1. A project, with billing attached

```bash
gcloud projects create gt2-relay-$RANDOM --name="GT2 Relay"
gcloud config set project <the-id-it-printed>
```

Billing has to be linked even to use the free tier — the console asks for a
card and does not charge it while you stay inside the allowances. Do it at
*Billing → Link a billing account*; there is no reliable CLI path for a first
account.

```bash
gcloud services enable compute.googleapis.com
```

That one takes a minute.

### 2. An address that will not move

The address is what players type into the **Server** box. An ephemeral one
changes whenever the instance stops, and then nobody can reach the room.

```bash
gcloud compute addresses create gt2relay-ip --region=us-central1
gcloud compute addresses describe gt2relay-ip --region=us-central1 \
  --format='value(address)'
```

Write down what it prints. A static address attached to a running instance
costs nothing; one left unattached is billed, so release it if you tear this
down.

For São Paulo, use `--region=southamerica-east1` here and
`--zone=southamerica-east1-a` below.

### 3. The machine

```bash
gcloud compute instances create gt2relay \
  --zone=us-central1-a \
  --machine-type=e2-micro \
  --image-family=debian-12 --image-project=debian-cloud \
  --boot-disk-size=30GB --boot-disk-type=pd-standard \
  --address=gt2relay-ip \
  --tags=gt2relay
```

`e2-micro`, `pd-standard` and 30 GB are exactly the Always Free shapes. Larger
anything and the free tier stops applying without warning.

### 4. One firewall, not two

This is where GCP is kinder than Oracle. The VPC rule is the only thing in the
way: Google's Debian images ship with **no host firewall rules at all**, so
there is no second `iptables` to discover afterwards.

```bash
gcloud compute firewall-rules create allow-gt2relay \
  --allow=udp:34720 \
  --target-tags=gt2relay \
  --source-ranges=0.0.0.0/0 \
  --description="GT2 rendezvous and relay"
```

`--target-tags=gt2relay` is why the instance was tagged: the rule opens the
port on that machine and on nothing else you ever create in this project.

### 5. Build it here, copy it there

Self-contained, so the VM needs no .NET installed:

```bash
dotnet publish GT2Relay -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -o out
```

One file, about 73 MB.

```bash
gcloud compute scp out/gt2relay gt2relay:~/ --zone=us-central1-a
gcloud compute ssh gt2relay --zone=us-central1-a
```

On the machine:

```bash
sudo mkdir -p /opt/gt2relay
sudo mv ~/gt2relay /opt/gt2relay/
sudo chmod +x /opt/gt2relay/gt2relay
/opt/gt2relay/gt2relay --port 34720
```

It should say:

```
gt2relay listening on udp/34720
rooms are forgotten after 30s without a publish
```

`Ctrl+C` once you have seen it.

### 6. Keep it running

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
sudo systemctl daemon-reload
sudo systemctl enable --now gt2relay
systemctl status gt2relay
```

`PrivateTmp=yes` matters more than it looks: a single-file .NET binary unpacks
itself into `/tmp` on the first run, and this gives it one of its own. If it
refuses to start, add
`Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/tmp/gt2relay` and create that
directory.

Nothing here needs backing up. Rooms live in memory and expire after thirty
seconds, so a restart costs the lobbies two seconds to rebuild themselves.

---

## Pointing the game at it

On both machines: open the multiplayer panel, paste the address into
**Server** under *Rooms on the internet*, press **Use**. Or, without touching
the interface:

```bash
$env:GT2_RELAY='<the address>'; ./bin/Debug/net10.0/GT2Port.exe
```

The log says `[relay] talking to <address>:34720` when the link comes up.

The host creates a room; the other machine sees it under *Rooms on the
internet* and joins. A room with a secret is not listed — the host reads out
the six-character code shown in its lobby, and the other player types it into
**Join by code**.

Neither of them opens a port.

## When nothing happens

In the order these actually go wrong:

**The firewall rule is missing or on the wrong tag.**

```bash
gcloud compute firewall-rules list --filter="name~gt2relay"
gcloud compute instances describe gt2relay --zone=us-central1-a \
  --format='value(tags.items)'
```

The rule's `--target-tags` and the instance's tags have to be the same word.

**Is anything arriving at all?** On the VM:

```bash
sudo tcpdump -n -i any udp port 34720
```

Nothing while a player is trying means the firewall. Traffic in with nothing
going back means the relay is not running — check `systemctl status gt2relay`.

**The address changed.** If the instance was stopped and the address was
ephemeral rather than reserved, it is a different address now:

```bash
gcloud compute instances describe gt2relay --zone=us-central1-a \
  --format='value(networkInterfaces[0].accessConfigs[0].natIP)'
```

**The room lists but never joins.** That is not the relay — the room's secret
is being refused, or it is full. The panel says which.

## Turning it off

```bash
gcloud compute instances delete gt2relay --zone=us-central1-a
gcloud compute addresses delete gt2relay-ip --region=us-central1
```

Delete the address too. Reserved and unattached is the one state Google
charges for.
