# Pi-hole setup

Preparing the Pi-hole host so its query log can be streamed to SQL Server without
interfering with DNS service.

Tested against **Pi-hole v6** on Debian 13 (trixie), arm64. Pi-hole v5 used a different
database layout and is not covered.

---

## What the loader reads

Pi-hole's FTL daemon writes every query to a SQLite database:

```
/etc/pihole/pihole-FTL.db
```

It is in WAL mode and is written continuously by a live service, so two rules matter:

1. **Open it read-only.** The loader uses `file:…?mode=ro` plus
   `PRAGMA query_only=ON`. Never open it read-write; a second writer can corrupt the
   WAL or block FTL.
2. **Run as the `pihole` user**, which owns the database and its WAL files. Running as
   root also works but is unnecessary; running as an unrelated user hits permission
   errors on the `-wal` and `-shm` files even when the main file is readable.

The loader reads FTL's `queries` **view** rather than the `query_storage` table — the
view resolves FTL's internal string interning, so `domain` and `client` come back as
text instead of integer IDs.

```bash
# There is no sqlite3 CLI on a stock Pi-hole; FTL ships its own.
sudo -u pihole pihole-FTL sqlite3 -readonly /etc/pihole/pihole-FTL.db \
  "SELECT COUNT(*), MIN(id), MAX(id) FROM query_storage;"
```

> Note the quoting: `pihole-FTL sqlite3` wants **single**-quoted SQL string literals.
> `"SELECT COUNT(*)||\" \"||MAX(id) …"` silently returns nothing, because SQLite treats
> double quotes as an *identifier*, not a string.

---

## 1. Confirm the Pi-hole version and database

```bash
pihole -v
ls -lh /etc/pihole/pihole-FTL.db*
sudo -u pihole pihole-FTL sqlite3 -readonly /etc/pihole/pihole-FTL.db \
  "SELECT COUNT(*) FROM query_storage;"
```

Expect three files: the database, plus `-wal` and `-shm`.

---

## 2. Give the host a static address

The loader connects out to SQL Server, and you will want to reach the host reliably. A
DHCP reservation or a static address both work.

With NetworkManager (Pi-hole v6 on Debian 13):

```bash
nmcli con show
sudo nmcli con mod "Wired connection 1" \
  ipv4.method manual \
  ipv4.addresses <PIHOLE_IP>/24 \
  ipv4.gateway <GATEWAY_IP> \
  ipv4.dns "<GATEWAY_IP>"
sudo nmcli con up "Wired connection 1"
```

Verify it actually came up with IPv4:

```bash
ip -4 addr show
ip route
```

> **If the interface comes up IPv6-only**, the static address you assigned is almost
> certainly already in use elsewhere on the network. NetworkManager refuses to claim a
> duplicate and leaves you with no IPv4 at all, which looks like a much stranger fault
> than it is. Check for a conflict before assuming anything else:
> ```bash
> sudo arping -D -I eth0 -c 3 <PIHOLE_IP>   # -D = duplicate address detection
> ip neigh | grep <PIHOLE_IP>
> ```
> A stray secondary IP on some other host — a domain controller with a leftover address,
> say — is a real and easy-to-miss cause. Also purge the stale reverse-DNS record
> afterwards, or name lookups will keep pointing at the wrong machine.

---

## 3. Decide whether Pi-hole should block or only log

Both are valid; the reports adapt.

```bash
grep -A3 '\[dns.blocking\]' /etc/pihole/pihole.toml
```

```toml
[dns.blocking]
active = false      # log-only: nothing is blocked, everything is recorded
```

| Mode | What the reports mean |
|------|----------------------|
| `active = true` | Blocked queries carry status 1/4/5/9/10/11. The *would-be-blocked* reports are near-empty because blocking already happened. |
| `active = false` | Nothing is blocked. The **would-be-blocked** reports become the interesting ones: they cross-reference every answered query against blocklist membership to show what *would* have been caught. |

Log-only mode is a good way to audit a blocklist before committing to it — you see the
collateral damage before anything breaks.

Apply a change with `sudo pihole restartdns`.

---

## 4. Make sure Pi-hole actually sees your clients

This is the step most often skipped, and it quietly invalidates every report: if
clients resolve DNS somewhere else, Pi-hole logs nothing for them and the warehouse
under-reports.

```bash
# Distinct clients seen in the last day - compare against what you expect
sudo -u pihole pihole-FTL sqlite3 -readonly /etc/pihole/pihole-FTL.db \
  "SELECT COUNT(DISTINCT client) FROM queries
   WHERE timestamp > strftime('%s','now','-1 day');"
```

Pick one of:

- **DHCP option 6** — point your router's DHCP DNS at `<PIHOLE_IP>`. Simplest, but
  devices with hard-coded resolvers (many smart TVs, Chromecast, some IoT) bypass it.
- **Port-53 redirect on the gateway** — a NAT rule sending all outbound TCP/UDP 53 to
  `<PIHOLE_IP>`. Catches the hard-coded offenders too.
- **Both**, which is what you want if the reports need to be trustworthy.

Neither stops DNS-over-HTTPS. A device using DoH to a public resolver is invisible to
Pi-hole regardless; blocking outbound 443 to known DoH endpoints is the only lever, and
it is out of scope here.

---

## 5. Retention on the Pi-hole side

Once rows are in SQL Server you no longer depend on FTL's retention, and the warehouse
is the long-term record. Keep the local window modest to keep the Pi responsive:

```toml
[database]
maxDBdays = 365      # FTL's own retention
DBinterval = 60      # seconds between flushes to disk
```

`DBinterval` sets the floor on end-to-end freshness: a query is not visible to the
loader until FTL flushes it. 60 s plus the loader's 5 s poll means "about a minute
behind live", which is fine for reporting. Lowering it increases SD-card writes.

---

## 6. SSH access for maintenance

Key-based access from the machine you administer from:

```bash
ssh-copy-id <PIHOLE_USER>@<PIHOLE_IP>
ssh <PIHOLE_USER>@<PIHOLE_IP> 'echo ok'
```

If you will script service management, passwordless sudo for the specific commands
saves interactive prompts:

```bash
sudo tee /etc/sudoers.d/pihole-sqlsync >/dev/null <<'SUDO'
<PIHOLE_USER> ALL=(ALL) NOPASSWD: /bin/systemctl restart pihole-sqlsync, \
                                  /bin/systemctl status pihole-sqlsync, \
                                  /bin/journalctl -u pihole-sqlsync *
SUDO
sudo visudo -c
```

> `visudo -c` before you log out. A malformed sudoers file can lock you out of sudo
> entirely.

---

## 7. Clock accuracy

Every row's timestamp comes from this host, so its clock defines your time axis.

```bash
timedatectl status
```

Confirm `System clock synchronized: yes` and `NTP service: active`. If not:

```bash
sudo apt install -y systemd-timesyncd
sudo timedatectl set-ntp true
```

> When timestamps look wrong, check the **workstation** you are reading reports from as
> well. A drifted desktop clock makes correct, current data look minutes stale — and it
> is easy to blame the pipeline for what is a local clock problem.

---

Next: [installing the sync pipeline](05-pihole-sqlsync.md).
