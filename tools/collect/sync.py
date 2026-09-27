#!/usr/bin/env python3
"""Stream Pi-hole FTL query log -> Microsoft SQL Server.

Reads new rows from the FTL SQLite `queries` view (read-only) and inserts them
into an MS SQL Server table, keyed on FTL's `id` so the load is idempotent.
Watermark is derived from MAX(id) in the target table, so restarts resume
cleanly with no local state. With --loop it backfills all history first, then
tails new queries forever.

Each row is also stamped with the client's MAC, FTL's raw hostname, and vendor
AS THEY WERE KNOWN AT THAT INSTANT (see client_mac/client_hostname/
client_vendor below). This is what makes reports correct across a DHCP
reassignment: dbo.DimClient is a single current-snapshot row per IP, refreshed
once a day, so any report that filtered by "the IP Allie's iPhone has right
now" over a multi-day window could include another device's queries from
whenever that IP belonged to someone else. Grouping by client_mac instead is
stable regardless of how many times the address has moved since.
"""
import argparse
import configparser
import datetime
import sqlite3
import sys
import time

import pymssql

from omada_lookup import OmadaLookup

CONFIG = "/etc/pihole-sqlsync/config.ini"

# FTL v6 query status enum -> label
STATUS_TEXT = {
    0: "unknown", 1: "gravity", 2: "forwarded", 3: "cache",
    4: "regex-deny", 5: "exact-deny", 6: "extern-blocked-ip",
    7: "extern-blocked-null", 8: "extern-blocked-nxra",
    9: "gravity-cname", 10: "regex-cname", 11: "denylist-cname",
    12: "retried", 13: "retried-dnssec", 14: "in-progress",
    15: "dbbusy", 16: "special-domain", 17: "cache-stale",
    18: "extern-blocked-ede15",
}

SELECT_COLS = ("id, timestamp, type, status, domain, client, forward, "
               "reply_type, reply_time, dnssec, ede")

INSERT_TMPL = (
    "INSERT INTO {t} (id, ts, type, status, status_text, domain, client, "
    "forward, reply_type, reply_time, dnssec, ede, "
    "client_mac, client_hostname, client_vendor) "
    "VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s)"
)

# How long a MAC's controller lookup result (or its absence) is trusted before
# trying again. A device that has never answered should not be asked on every
# single row it generates - that is the same kind of hot-path network call
# this design otherwise avoids.
OMADA_RETRY_SECONDS = 3600
# How often the "does this MAC already have a good name" cache is refreshed
# from SQL Server. A local dict read against a few hundred rows, not a
# per-batch cost worth worrying about.
KNOWN_NAMES_REFRESH_SECONDS = 300


def load_cfg():
    c = configparser.ConfigParser()
    if not c.read(CONFIG):
        sys.exit(f"[fatal] cannot read config {CONFIG}")
    return c


def connect_mssql(cfg):
    m = cfg["mssql"]
    return pymssql.connect(
        server=m["server"], port=m.get("port", "1433"),
        user=m["user"], password=m["password"], database=m["database"],
        autocommit=False, timeout=60, login_timeout=30,
    )


def open_ftl(path):
    con = sqlite3.connect(f"file:{path}?mode=ro", uri=True, timeout=30)
    # FTL occasionally logs a domain containing non-UTF-8 bytes. The default
    # text_factory raises on those, which kills the process before the
    # watermark advances -- so the same batch is retried forever. Substitute
    # U+FFFD instead of raising.
    con.text_factory = lambda b: b.decode("utf-8", "replace")
    con.execute("PRAGMA query_only=ON")
    con.execute("PRAGMA busy_timeout=5000")
    return con


def load_client_map(ftl):
    """ip -> (mac, hostname, vendor) as FTL currently has it.

    Same source dims.py reads (network_addresses joined to network), but read
    fresh on our own cadence rather than trusting dims.py's daily snapshot -
    this is what lets client_mac/client_hostname/client_vendor reflect the
    instant a row is ingested, not whenever the dimension last refreshed.
    Raw and unfiltered (no ambiguous-name rejection): that filtering is a
    display-name concern for dbo.vDeviceName, not this audit stamp.
    """
    rows = ftl.execute(
        "SELECT na.ip, n.hwaddr, na.name, n.macVendor "
        "FROM network_addresses na LEFT JOIN network n ON na.network_id = n.id"
    ).fetchall()
    out = {}
    for ip, mac, name, vendor in rows:
        out[ip] = (
            mac.lower() if mac else None,
            (name or "").strip() or None,
            (vendor or "").strip() or None,
        )
    return out


def insert_batch(mssql, insert_sql, data):
    """Insert a batch, falling back to row-by-row if the batch fails, so a
    single bad row is skipped (and logged) instead of stalling the stream.
    Returns the number of rows actually written."""
    mcur = mssql.cursor()
    try:
        try:
            mcur.executemany(insert_sql, data)
            mssql.commit()
            return len(data)
        except Exception as e:
            mssql.rollback()
            print(f"[warn] batch of {len(data)} failed ({type(e).__name__}: {e});"
                  f" retrying row-by-row", flush=True)
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


def get_watermark(mssql, table):
    cur = mssql.cursor()
    cur.execute(f"SELECT ISNULL(MAX(id),0) FROM {table}")
    wm = cur.fetchone()[0] or 0
    cur.close()
    return wm


def load_known_macs(mssql):
    """MACs dbo.vDeviceName already has an opinion on. Used only to decide
    whether a controller lookup is worth attempting - not authoritative for
    anything written to the fact table."""
    try:
        cur = mssql.cursor()
        cur.execute("SELECT mac FROM dbo.vDeviceName")
        macs = {r[0].lower() for r in cur.fetchall()}
        cur.close()
        return macs
    except Exception as e:
        print(f"[warn] could not refresh known-name cache: {type(e).__name__}: {e}",
              flush=True)
        return set()


def record_omada_name(mssql, mac, name, source):
    """Write one on-demand Omada resolution into dbo.DeviceNameObservation.

    Targeted replace (this MAC, this source only) rather than the wholesale
    delete-and-reload Collect-OmadaNames.ps1 does for its full poll - this is
    one device resolved early, not a fresh snapshot of every device.
    """
    try:
        cur = mssql.cursor()
        cur.execute(
            "DELETE FROM dbo.DeviceNameObservation WHERE mac = %s AND source = %s",
            (mac, source))
        cur.execute(
            "INSERT INTO dbo.DeviceNameObservation (mac, source, name, observed_utc) "
            "VALUES (%s, %s, %s, SYSUTCDATETIME())",
            (mac, source, name))
        mssql.commit()
        cur.close()
        print(f"[omada] resolved {mac} -> '{name}' ({source})", flush=True)
    except Exception as e:
        mssql.rollback()
        print(f"[warn] could not record omada name for {mac}: {type(e).__name__}: {e}",
              flush=True)


def clip(v, n=255):
    return None if v is None else str(v)[:n]


def transform(rows, client_map):
    out = []
    for (qid, ts, qtype, status, domain, client, forward,
         reply_type, reply_time, dnssec, ede) in rows:
        dt = (datetime.datetime.fromtimestamp(ts, datetime.timezone.utc)
              .replace(tzinfo=None)) if ts is not None else None
        mac, hostname, vendor = client_map.get(client, (None, None, None))
        out.append((qid, dt, qtype, status, STATUS_TEXT.get(status, str(status)),
                    clip(domain), clip(client), clip(forward),
                    reply_type, reply_time, dnssec, ede,
                    clip(mac, 32), clip(hostname), clip(vendor, 128)))
    return out


def run(loop, interval, batch):
    cfg = load_cfg()
    table = cfg["mssql"].get("table", "dbo.PiholeQueries")
    ftl_path = cfg["source"]["ftl_db"]
    insert_sql = INSERT_TMPL.format(t=table)

    ftl = open_ftl(ftl_path)
    mssql = connect_mssql(cfg)
    watermark = get_watermark(mssql, table)
    print(f"[start] table={table} watermark(id)={watermark}", flush=True)

    omada = OmadaLookup()
    known_macs = load_known_macs(mssql)
    known_macs_loaded = time.monotonic()
    attempted = {}  # mac -> monotonic time of last controller lookup

    total = 0
    while True:
        if time.monotonic() - known_macs_loaded > KNOWN_NAMES_REFRESH_SECONDS:
            known_macs = load_known_macs(mssql)
            known_macs_loaded = time.monotonic()

        client_map = load_client_map(ftl)

        cur = ftl.execute(
            f"SELECT {SELECT_COLS} FROM queries WHERE id > ? ORDER BY id LIMIT ?",
            (watermark, batch),
        )
        rows = cur.fetchall()
        if rows:
            data = transform(rows, client_map)
            wrote = insert_batch(mssql, insert_sql, data)
            watermark = rows[-1][0]
            total += wrote
            print(f"[sync] +{wrote} rows  watermark(id)={watermark}  "
                  f"session_total={total}", flush=True)

            if omada.available:
                now = time.monotonic()
                seen_macs = {row[12] for row in data if row[12]}
                for mac in seen_macs - known_macs:
                    if now - attempted.get(mac, 0) < OMADA_RETRY_SECONDS:
                        continue
                    attempted[mac] = now
                    found = omada.resolve(mac)
                    if found:
                        name, source = found
                        record_omada_name(mssql, mac, name, source)
                        known_macs.add(mac)
            continue  # keep draining until caught up
        if not loop:
            break
        time.sleep(interval)
    print(f"[done] session_total={total}", flush=True)


def main():
    ap = argparse.ArgumentParser(description="Pi-hole FTL -> MS SQL Server streamer")
    ap.add_argument("--loop", action="store_true",
                    help="keep tailing after backfill (service mode)")
    ap.add_argument("--interval", type=float, default=5.0,
                    help="seconds to sleep when caught up")
    ap.add_argument("--batch", type=int, default=5000,
                    help="rows per insert transaction")
    args = ap.parse_args()
    try:
        run(args.loop, args.interval, args.batch)
    except Exception as e:  # let systemd Restart=always handle recovery
        print(f"[error] {type(e).__name__}: {e}", file=sys.stderr, flush=True)
        sys.exit(1)


if __name__ == "__main__":
    main()
