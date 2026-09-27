# Device names and the DNS push

How a device gets a name, where that name is stated, and how it reaches AD DNS.

## The fault this closes

AD DNS on `bylotas.net` was accumulating junk, and the cause was not DNS.

A scheduled task on WINAD02, **`OmadaDNSUpdate`**, ran `C:\Scripts\UpdateOmadaDNS.ps1`
every five minutes as the `joshua` domain admin account. Line 353 of that script was:

```powershell
$hostname = $client.name      # Omada's client name, taken verbatim
```

Omada carries two names in one object: one a person typed, and one the device
announced at DHCP. `name` is usually the announced one, and for IoT gear the
announced one is frequently the device's own MAC. So every five minutes the task
wrote records like `B0-BE-76-B2-C7-22` into the zone. All 88 MAC-named record
objects were owned by `BYLOTAS\Domain Admins`, which is what identified the task
rather than the devices as the author. 47 of them had already been deleted once
and had come back.

Two things made it worse:

- The script de-duplicated by **hostname only**, never by address. Every DHCP move
  left the previous name behind, so 23 addresses ended up carrying more than one
  name. `10.20.0.227` carried five.
- It skipped PTR creation when the reverse zone was missing, and only
  `0.20.10.in-addr.arpa` exists. Clients on `10.20.1.x` therefore had no PTR at
  all, so reverse lookups for them failed and fell back to junk.

Scavenging was **not** the fix, and enabling it would have wasted the effort: it
is off on both DCs, but no dynamic record was older than 30 days, because the
task refreshed the junk every five minutes. Nothing could ever age out.

`OmadaDNSUpdate` was disabled on 2026-09-14. **Do not re-enable it** — this
pipeline replaces it.

## The pieces

```
devices ──► discover_network.py (linserver05) ──┐
Omada   ──► Collect-OmadaNames.ps1  (WINAD02) ──┤──► dbo.DeviceNameObservation
AD DNS  ──► Collect-AdDnsNames.ps1  (WINAD02) ──┘              │
                                                               ▼
                                              dbo.DeviceTruth ──► dbo.vDeviceTruth
                                            (the Editor writes)        │
                                                                       ▼
                                          dbo.DnsPushRequest ──► Push-DeviceDns.ps1
                                                (the button)        (WINAD02) ──► AD DNS
```

### `dbo.vDeviceTruth` — the one view to reference

One row per MAC: `mac`, `current_ip`, `device_name`, plus where each half of the
answer came from. This is what to join to; everything else is how it is produced.

`device_name` is the name stated in the Editor, falling back to the resolved name
from precedence. `current_ip` is the most recent observation that carried an
address, whatever source saw it — which is why the discovery sweep runs every 30
minutes.

### `dbo.DeviceTruth` — what a person stated

Its own table, deliberately. The collectors **replace their source wholesale** on
every run, so a typed name stored as a `manual` observation would survive until
the next collection and then vanish. Constrained to a legal DNS label, because
that is where it is going, and a MAC-shaped name is rejected outright.

Underscore is allowed: it is not legal per RFC 1123, but Microsoft DNS accepts it
and the zone already contains `GE_Light_687B`.

### Name precedence

Set in `dbo.vDeviceName`:

| | Source | Why there |
|---|---|---|
| 1 | `manual` | Stated by a person in the Editor. Nothing outranks it. |
| 2 | `omada` | Typed into the controller by a person. |
| 3 | `kasa` | The alias typed into the Kasa app when the device was installed. |
| 4 | `mdns` | Published over Bonjour, often from the TXT `name=` field. |
| 5 | `ssdp` | UPnP `friendlyName` — how a TV or plug identifies itself. |
| 6 | `snmp` | `sysName`. Printers and switches answer this and mean it. |
| 7 | `netbios` | The Windows name, from the node itself. |
| 8 | `addns` | An A record in the forward zone. |
| 9 | `omadadhcp` | A hostname the device announced at DHCP. `wlan0` lives here. |
| 10 | `nmap` | Reverse DNS seen during the sweep. |
| 11 | `ftl` | Pi-hole reverse DNS. The source of the `ALIEN01` collision. |

mDNS and SSDP outrank AD DNS on purpose: both are names the vendor or the owner
set on the device, whereas an IoT device's A record is whatever its DHCP
announcement registered. Domain-joined machines publish no mDNS, so their A
record still wins.

## Discovery — `tools/collect/discover_network.py`

Runs on **linserver05**, because ARP only answers on-link and an ARP reply is the
only thing here that ties an address to a MAC without trusting a lease table or a
DNS record. Needs `nmap` and `avahi-utils`; reuses `/etc/pihole-sqlsync/config.ini`
for its SQL credentials.

| Probe | Yields |
|---|---|
| `nmap -sn -R` sweep | MAC ↔ IP (this is what makes `current_ip` current), reverse DNS |
| `avahi-browse` | mDNS instance names and TXT `name=` / `md=` |
| SSDP M-SEARCH + description fetch | UPnP `friendlyName` |
| `nmap --script nbstat` | NetBIOS name |
| `nmap --script snmp-*` | `sysName` |
| **Kasa TCP 9999** | `alias` — the name typed in the Kasa app — plus model |
| `nmap -sV -O` (nightly only) | `device_type` for devices that will not say |

Scheduled by `tools/collect/systemd/`: every 30 minutes without the fingerprint
pass, and a thorough run nightly at 03:40.

### Why there is no MAC-to-model API

A MAC carries only the OUI — the vendor. The low 24 bits are a serial no vendor
publishes, so no lookup service turns `20:e1:5d:32:c5:38` into "HS200 Kitchen
Light"; they all return the vendor string, which nmap already supplies offline.
Sending the MAC inventory to a third party would also put the device list off the
network, which is the opposite of this server's premise. Asking the device
directly is both better and local — that is what the Kasa, mDNS, SSDP and SNMP
probes are.

### Names that are rejected

Filtered in the collector and again in the view, because a bad name that outranks
a good one is worse than no name:

- A MAC in any spelling, including the separatorless `b0be76b2c722`.
- A long unbroken hex run — `XB5839F36FA6C`, `8EFFE82DA7B72D4F`. Short readable
  tails survive: `GE_Light_687B`, `Ring-a7ed55`.
- Network-stack names: `wlan0`, `eth0`, `localhost`.
- Platform-plus-serial: `amazon-1210-45928`, `android-a1b2c3d4`, `linux-27`.
- Capability names several devices publish: `SpotifyConnect` came from three
  different devices in one sweep.
- A name that is just an IP address — some mDNS TXT records set `name=` to it.
- **A name a source claims for more than one MAC.** Reverse DNS gave `ALIEN01` to
  13 MACs in a single sweep, and two Amazon devices both answered `amzn`. NetBIOS
  is exempt: `WINSERVER01` answering on both its NICs is one machine, not a
  collision.

## The Editor — `/Devices`

Readable by any viewer; writing requires the `Report.DeviceEditor` app role. Each
row shows the current address, the vendor and model, what AD DNS currently says,
and every candidate name any source offered as a one-click chip — unfiltered, so
seeing that the controller only ever knew a device as `wlan0` is available as
context.

`needs_review` is the worklist: no stated name, and nothing better than the device
talking about itself.

## The push — `tools/dns/Push-DeviceDns.ps1`

The button does not write DNS. It inserts a row in `dbo.DnsPushRequest`; the
`PiholeReport-DnsPush` task on WINAD02 claims it, acts, and writes every record it
touched to `dbo.DnsPushLog`.

That split exists so the web application never holds write rights on the zone. A
web app that can edit DNS is a web app that can redirect the network. The agent
runs as **SYSTEM**, reaching SQL as `BYLOTAS\WINAD02$` — verified able to add and
remove records, so unlike the task it replaces it needs **no stored password**.

Only names with `name_source = 'manual'` are published. A resolved name is a good
guess for a report; it is not something to write into DNS on the strength of a
device's own claim about itself.

For each publishable device it ensures the A record, then **removes other dynamic
A records pointing at the same address** — the fix for the 23 multi-named
addresses — then ensures the PTR, creating the reverse zone with aging enabled
when it is missing.

### What it will never touch

- **A static record.** No timestamp means a person created it deliberately:
  `www`, `pihole`, `filter`, `filtering` and `minecraftapi` are all aliases on an
  address that also has a dynamic name. Without this rule, "remove the other
  names on this address" would delete them.
- The zone apex, `DomainDnsZones`, `ForestDnsZones`, anything under `_msdcs`.
- A name stated for more than one MAC — skipped and reported, since it cannot
  point at two addresses.

### Always dry run first

`dry_run` works out and records every decision and changes nothing. A real dry
run for one device produced:

```
added   A    Boys-Reading-Light-Tower              10.20.0.152
removed A    B0-BE-76-B2-C7-22                     10.20.0.152   MAC-named leftover
removed A    HS105                                 10.20.0.152   generic model name
removed PTR  A4-08-01-41-6B-F4.bylotas.net         10.20.0.152   another device entirely
added   PTR  Boys-Reading-Light-Tower.bylotas.net  10.20.0.152
```

That device had a real name all along — `Boys-Reading-Light-Tower`, set in the
Kasa app. Nothing had ever asked it.

## Writing back to the Omada controller

The push agent also sets the client `name` in Omada, so the controller shows
`Basement-Stairs` instead of the model string it derives from the DHCP
hostname (`HS210`, `HS200`, `KL125`, `HS105`).

What the OpenAPI can and cannot do, established by asking each route what
methods it accepts rather than by reading documentation:

| Route | Methods | |
|---|---|---|
| `/clients/{mac}/name` | `PATCH` | **used by the push agent** |
| `/clients/{mac}/ratelimit` | `PATCH` | not used |
| `/clients/{mac}/block`, `/unblock`, `/lock-to-ap` | `POST` | not used |
| `/clients/{mac}` | `GET, HEAD, DELETE` | read only - and see the warning below |
| anything for the address reservation | none | ~20 spellings tried, all 404 |
| anything for a description or location | none | the field exists, nothing writes it |

So **address reservations are manual**. `ipSetting.useFixedAddr` is readable on
the client object and has no writable endpoint. `tools/dns/Get-OmadaReservations.ps1`
prints the MAC and address for every device with a stated name, ordered the way
the controller lists clients, and drops a CSV in the OneDrive project folder.
There is also **no description field** on an Omada client - only `name`,
`hostName` and `systemName` - so the Editor's `notes` stay in SQL.

Raising the API key's scope does not change any of this. The refusals are `405
Method Not Allowed` from the router with an `Allow` header, not `403` or an
Omada `errorCode`; the verbs do not exist at any scope.

> **DELETE on `/clients/{mac}` is live and unguarded.** It was issued during a
> method sweep and the controller returned `{"errorCode":0,"msg":"Success."}`.
> It forgets the client: the record is dropped and rebuilt when the device next
> talks, so a hand-typed name and any custom settings on it are lost. Nothing
> was lost that time only because the client had neither. Never put DELETE in a
> discovery loop against this API.

### Provenance, once the name is written back

`Collect-OmadaNames` then reads that name back as source `omada` - "typed by a
person", which it is, but by us. It is an echo of `dbo.DeviceTruth`, not
independent corroboration, so do not read agreement between `manual` and
`omada` as two sources concurring. `manual` outranks `omada` anyway, so
resolution is unaffected.

### Two things that cost real time here

**`$PSScriptRoot` is empty inside a `param()` default under
`powershell.exe -File` on 5.1.** A default of
`(Join-Path $PSScriptRoot 'omada.json')` threw during parameter binding - before
the `trap` existed - so the run wrote no event, never claimed its request, and
exited 1. It works under `-Command`, which made it look intermittent. Resolve
paths like that in the script body, as `Collect-OmadaNames.ps1` does.

**`DnsPushLog.record_type` was `varchar(4)`,** sized for `A`, `PTR` and `zone`.
`omada` is five characters, so SQL truncated it to `omad` and failed the check
constraint - every controller write logged as an error while the write itself
had succeeded. The column is `varchar(8)` now, and the PowerShell `ValidateSet`
on `Write-PushLog` has to stay in step with `CK_DPL_type`.
## Setup

```powershell
# 1. Schema (idempotent)
#    tools/schema/DeviceTruth.sql against the pihole database

# 2. Discovery on linserver05
sudo apt-get install -y nmap avahi-utils
#    copy tools/collect/discover_network.py to /opt/pihole-discovery/
#    copy tools/collect/systemd/* to /etc/systemd/system/ and enable the timers

# 3. Push agent on WINAD02
#    copy tools/dns/Push-DeviceDns.ps1 to C:\Scripts\PiholeCollect\
#    register PiholeReport-DnsPush as SYSTEM, repeating every 2 minutes

# 4. Entra: add the Report.DeviceEditor app role and assign it
```

## Point-in-time client attribution

**The fault this closes:** `dbo.DimClient` and `dbo.vClient` are keyed by IP - one
row per address, overwritten by `dims.py` once a day. Every client-facing report
resolved a device (say, "Allie's iPhone") to whichever single IP currently maps
to it, then filtered `dbo.PiholeQueries` by that IP across the whole requested
date range. DHCP reassigns IPs over time, so a report window spanning a
reassignment pulled in another device's queries too - observed as another
machine's traffic (`OMEN01`) appearing under a phone's report.

**The fix:** `sync.py` now stamps three columns onto every row **as it is
ingested**, from FTL's own `network`/`network_addresses` tables (a local,
no-network-call read) - the moment the IP-to-MAC mapping is known with
certainty, rather than reconstructed later against a daily snapshot:

| Column | What it is |
|---|---|
| `client_mac` | The device's MAC at that instant. **What every client-facing report should filter and group on** - a stable identity, unlike `client` (the IP). |
| `client_hostname` | FTL's raw, unfiltered name at that instant. An audit column, like `DimClient.reported_name` - not the display name. |
| `client_vendor` | OUI vendor at that instant, for a device with no name at all. |

Rows ingested before this migration (`tools/schema/QueryClientAttribution.sql`)
have no `client_mac` - there is no reliable way to reconstruct one after the
fact, so they are left NULL rather than guessed at, and age out of the default
7-day report window quickly. `client-detail.sql`, `top-clients.sql`,
`would-be-blocked-by-client.sql`, and the query builder (`BuilderSqlComposer`,
`ClientDirectory`) all group/filter by `client_mac` now, joining
`dbo.vDeviceName` for the current best display name rather than `dbo.vClient`.

### On-demand Omada lookup

Waiting up to 30 minutes for `discover_network.py`'s sweep - or on
`Collect-OmadaNames.ps1`, currently broken - to name a newly-seen device is
slow. `sync.py` now also calls the Omada Controller's read-only OpenAPI
directly (`tools/collect/omada_lookup.py`, `GET .../clients/{mac}` - a single
client, not the paged full-list poll `Collect-OmadaNames.ps1` does) for any
`client_mac` that `dbo.vDeviceName` doesn't already have a good name for, and
writes the result into `dbo.DeviceNameObservation` as source `omada` /
`omadadhcp`, same shape that script produces.

This stays out of the hot ingest path: it is throttled per MAC (one attempt
per hour while unresolved, `OMADA_RETRY_SECONDS` in `sync.py`), driven off a
`dbo.vDeviceName` cache refreshed every 5 minutes, not attempted per row.

Requires its own credential at `/etc/pihole-sqlsync/omada.json` on
linserver05 - a separate VIEW-scoped OpenAPI app from the one
`Collect-OmadaNames.ps1` uses on WINAD02 (Settings → Platform Integration →
OpenAPI → Add New App → Client Mode → VIEW permission), so this host holds
its own independently-revocable read-only credential. Missing file = feature
quietly disabled, not an error - `sync.py` must keep ingesting either way.

## Still outstanding

- **Both name collectors are failing**, so `addns` and `omada` observations are
  stale. `Collect-OmadaNames` fails with "the login is from an untrusted domain
  and cannot be used with Integrated authentication"; `Collect-AdDnsNames` fails
  with a post-login connection timeout to WINSERVER01. Until these are fixed the
  controller's typed names are not refreshing.
- Scavenging is off on both DCs and aging is off on `0.20.10.in-addr.arpa`. Worth
  turning on once the junk stops being rewritten, so future leftovers age out
  without a push.
- `UpdateOmadaDNS.ps1` is disabled but still present and still scheduled. Decide
  whether to delete the task outright.
- The `pihole` collector login is `db_owner`, which is far more than it needs and
  undercuts the read-only argument the SQL console's safety rests on.
