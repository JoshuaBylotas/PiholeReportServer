# The sync pipeline — `pihole-sqlsync`

The loader that fills the warehouse: a small Python service on the Pi-hole host that
tails FTL's query log and streams new rows into SQL Server.

It is deliberately simple. There is no local state file, no message queue and no
scheduler — just a watermark derived from the destination table, which makes it
idempotent and self-healing across restarts.

---

## How it works

```
FTL queries view ──► sync.py ──► dbo.PiholeQueries
     (SQLite, ro)     watermark      (SQL Server)
                    = MAX(id) in destination
```

1. On start, ask SQL Server for `ISNULL(MAX(id), 0)`. That is the watermark; no local
   state to lose.
2. `SELECT … FROM queries WHERE id > @watermark ORDER BY id LIMIT @batch`
3. Transform: unix timestamp → UTC `datetime`, status code → label.
4. `executemany` INSERT in one transaction; commit; advance the watermark.
5. Repeat until caught up, then sleep `--interval` seconds and poll again.

Because the destination has a primary key on FTL's `id`, re-running can never duplicate
rows. Because the watermark is read from the destination, a crash loses nothing.

**Freshness** ≈ FTL's `DBinterval` (default 60 s) + the poll interval (5 s).

---

## Install

### 1. Dependencies

```bash
sudo apt update
sudo apt install -y python3 python3-pymssql
```

> **Use `pymssql`, not `pyodbc`/`msodbcsql18`.** Microsoft's ODBC driver has no clean
> build for Debian 13 on arm64, and chasing it is a dead end on a Raspberry Pi.
> `pymssql` needs no external driver.

If the distro package is unavailable:

```bash
sudo apt install -y python3-pip freetds-dev
sudo pip3 install --break-system-packages pymssql
```

### 2. Layout

```bash
sudo mkdir -p /opt/pihole-sqlsync /etc/pihole-sqlsync
sudo install -o pihole -g pihole -m 0755 sync.py /opt/pihole-sqlsync/sync.py
sudo install -o pihole -g pihole -m 0755 dims.py /opt/pihole-sqlsync/dims.py
```

### 3. Configuration

```bash
sudo tee /etc/pihole-sqlsync/config.ini >/dev/null <<'INI'
[mssql]
server   = <SQL_HOST>
port     = 1433
database = <SQL_DB>
user     = pihole_ingest
password = <PASSWORD>
table    = dbo.PiholeQueries

[source]
ftl_db = /etc/pihole/pihole-FTL.db
INI

# This file holds a password. Lock it down.
sudo chown pihole:pihole /etc/pihole-sqlsync/config.ini
sudo chmod 600 /etc/pihole-sqlsync/config.ini
```

> **Check the mode.** A config file created with a default umask lands as `0644` —
> world-readable — and it contains a database password. Verify:
> ```bash
> stat -c '%a %U:%G %n' /etc/pihole-sqlsync/config.ini   # want: 600 pihole:pihole
> ```

The loader needs **write** access, so it uses its own login — not the report server's
read-only one:

```sql
CREATE LOGIN pihole_ingest WITH PASSWORD = '<PASSWORD>';
USE <SQL_DB>;
CREATE USER pihole_ingest FOR LOGIN pihole_ingest;
ALTER ROLE db_datawriter ADD MEMBER pihole_ingest;
ALTER ROLE db_datareader ADD MEMBER pihole_ingest;
```

### 4. Backfill

Drain history first, in a detached session — with tens of millions of rows this takes a
while and you do not want an SSH drop to kill it.

```bash
sudo -u pihole nohup python3 /opt/pihole-sqlsync/sync.py > /tmp/backfill.log 2>&1 &
tail -f /tmp/backfill.log
```

Roughly 2,000–2,500 rows/second is typical for a Pi writing to SQL Server over a LAN,
so budget about 2.5 hours per 20M rows. Build the reporting indexes
([SQL setup](03-sql-server-setup.md#indexes)) **after** this completes.

### 5. Run it as a service

```bash
sudo tee /etc/systemd/system/pihole-sqlsync.service >/dev/null <<'UNIT'
[Unit]
Description=Stream Pi-hole FTL query log to MS SQL Server
After=network-online.target pihole-FTL.service
Wants=network-online.target

[Service]
User=pihole
ExecStart=/usr/bin/python3 /opt/pihole-sqlsync/sync.py --loop --interval 5
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
UNIT

sudo systemctl daemon-reload
sudo systemctl enable --now pihole-sqlsync
systemctl status pihole-sqlsync
```

---

## The bad-row failure mode — read this before you trust `Restart=always`

This is the one non-obvious operational hazard, and it will happen eventually.

Pi-hole occasionally logs a domain containing **bytes that are not valid UTF-8** —
typically a malformed query from a misbehaving client, something like
`192.0.2.58:443<0x??>http`. Python's `sqlite3` module defaults to
`text_factory = str`, which *raises* on undecodable text rather than substituting:

```
OperationalError: Could not decode to UTF-8 column 'domain' with text '...'
```

The naive service definition turns that single bad row into a **permanent deadlock**:

1. The exception kills the process before the batch commits.
2. The watermark therefore never advances.
3. `Restart=always` starts a fresh process, which reads *the same batch*, hits the same
   byte, and dies again.

The loop is silent unless you look — the service reports as "activating", not "failed",
and the restart counter climbs into the thousands. Meanwhile the warehouse quietly
stops receiving data while `MAX(ts)` sits frozen days in the past.

**Two fixes, both belong in `sync.py`:**

```python
def open_ftl(path):
    con = sqlite3.connect(f"file:{path}?mode=ro", uri=True, timeout=30)
    # FTL occasionally logs a domain containing non-UTF-8 bytes. The default
    # text_factory raises on those, which kills the process before the watermark
    # advances - so the same batch is retried forever. Substitute instead.
    con.text_factory = lambda b: b.decode("utf-8", "replace")
    con.execute("PRAGMA query_only=ON")
    return con
```

```python
def insert_batch(mssql, insert_sql, data):
    """Insert a batch; on failure retry row-by-row so one bad row cannot wedge
    the stream. Returns the number of rows actually written."""
    mcur = mssql.cursor()
    try:
        try:
            mcur.executemany(insert_sql, data)
            mssql.commit()
            return len(data)
        except Exception as e:
            mssql.rollback()
            print(f"[warn] batch failed ({type(e).__name__}: {e}); row-by-row", flush=True)
        ok = 0
        for row in data:
            try:
                mcur.execute(insert_sql, row)
                mssql.commit()
                ok += 1
            except Exception as e:
                mssql.rollback()
                print(f"[skip] id={row[0]}: {type(e).__name__}: {e}", flush=True)
        return ok
    finally:
        mcur.close()
```

The watermark must advance **whether or not** every row inserted. Skipping one bad row
and logging it is strictly better than stopping the pipeline.

The substituted `U+FFFD` lands in the `varchar` column as `?`. The
[malformed-domains report](07-reports.md) surfaces those rows so the substitution is
visible rather than silent.

### Detecting a stall

```bash
# Restart counter climbing = wedged, not healthy
systemctl show pihole-sqlsync -p NRestarts --value

# Is the watermark still moving?
journalctl -u pihole-sqlsync -n 5 --no-pager | grep '\[sync\]'
```

```sql
-- Anything over a few minutes means the loader is not keeping up
SELECT MAX(ts) AS newest,
       DATEDIFF(minute, MAX(ts), SYSUTCDATETIME()) AS lag_minutes
FROM dbo.PiholeQueries;
```

The **Ingest freshness** report does exactly this, including a scan for empty hours in
the last week — a stall that has since been fixed leaves a hole, not a stopped clock.

Reset the counter after fixing something, so the next stall is obvious:

```bash
sudo systemctl reset-failed pihole-sqlsync
```

---

## Dimension refresh — `dims.py`

Dimensions are snapshots, rebuilt on a timer rather than streamed.

```bash
sudo tee /etc/systemd/system/pihole-sqlsync-dims.service >/dev/null <<'UNIT'
[Unit]
Description=Refresh Pi-hole reporting dimension tables
After=network-online.target

[Service]
Type=oneshot
User=pihole
ExecStart=/usr/bin/python3 /opt/pihole-sqlsync/dims.py
UNIT

sudo tee /etc/systemd/system/pihole-sqlsync-dims.timer >/dev/null <<'UNIT'
[Unit]
Description=Daily Pi-hole dimension refresh

[Timer]
OnCalendar=daily
Persistent=true

[Install]
WantedBy=timers.target
UNIT

sudo systemctl daemon-reload
sudo systemctl enable --now pihole-sqlsync-dims.timer
sudo systemctl start pihole-sqlsync-dims.service     # first run now
systemctl list-timers pihole-sqlsync-dims.timer
```

What it builds, from Pi-hole's own `network`, `network_addresses` and `gravity.db`:

| Table | Source | Typical size |
|-------|--------|--------------|
| `DimStatus` | static map | ~19 rows |
| `DimType` | static map, decoding FTL's `OTHER` as 100 + qtype | ~30 rows |
| `DimClient` | `network` + `network_addresses` | one row per known device |
| `Adlists` | `gravity.db` → `adlist` | one row per subscribed list |
| `GravityDomains` | `gravity.db` → `gravity` | millions |

> **Run `dims.py` after any large backfill.** If the fact table was frozen or catching
> up while the timer fired, the dimensions describe a narrower period than the facts
> and clients that appeared during the backfill will show as `(unknown)`.

---

## Operating notes

```bash
# Follow it live
journalctl -u pihole-sqlsync -f

# Recent problems only
journalctl -u pihole-sqlsync --since '24 hours ago' | grep -E '\[warn\]|\[skip\]|\[error\]'

# Restart safely - the watermark is re-derived, so this is always safe
sudo systemctl restart pihole-sqlsync
```

Reconciliation, source against destination:

```bash
sudo -u pihole pihole-FTL sqlite3 -readonly /etc/pihole/pihole-FTL.db \
  "SELECT COUNT(*), MAX(id) FROM query_storage;"
```

```sql
SELECT COUNT_BIG(*), COUNT_BIG(DISTINCT id), MAX(id) FROM dbo.PiholeQueries;
```

Compare `MAX(id)` on both sides — that is the meaningful check. Row counts drift by a
few hundred between the two queries simply because FTL keeps logging, so take them in
one statement each and do not read too much into a small difference.

`COUNT(*)` should equal `COUNT(DISTINCT id)` always; the primary key guarantees it.

### Resource use

Negligible. A few MB of RSS, and CPU only in bursts while a batch is in flight. The
loader is I/O-bound on the SQL round-trip, not on the Pi.

The one thing to watch is **SD-card wear** if you lowered FTL's `DBinterval` — the
loader itself only reads.
