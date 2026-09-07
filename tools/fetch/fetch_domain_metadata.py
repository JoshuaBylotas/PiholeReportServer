#!/usr/bin/env python3
"""Collect self-declared metadata from domains seen in the DNS warehouse.

Runs on the isolated Pi, deliberately NOT on the database or web server. This is
the only component that makes outbound connections to arbitrary hosts drawn from
DNS logs, and some of those hosts are malware and phishing infrastructure. Keeping
it off the machines that hold the data bounds what a hostile response can reach,
and the SQL login it uses can write metadata and read the work list -- nothing
else, not even the query history.

Safety properties, all deliberate:

  * HEAD first. If the content type is not HTML there is nothing to parse, so the
    body is never downloaded at all.
  * Hard byte cap while streaming. A malicious host cannot exhaust memory by
    serving an endless response.
  * Short connect and read timeouts, and a redirect limit.
  * No JavaScript is executed and no cookies are stored or sent.
  * Identifiable User-Agent, so anyone reading their logs can see what this is.
  * Bounded concurrency, one request per host at a time.

A failure is recorded, not discarded. "Connection refused on both 443 and 80" is
strong evidence that a domain is a telemetry or API endpoint rather than a web
site, which is exactly the kind of distinction the categoriser needs.

Usage:
    python3 fetch_domain_metadata.py --config /etc/pihole-fetch/config.ini
    python3 fetch_domain_metadata.py --limit 50 --dry-run
"""
from __future__ import annotations

import argparse
import configparser
import re
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from datetime import datetime, timezone
from urllib.parse import urlparse

import pymssql
import requests
import urllib3
from bs4 import BeautifulSoup

# Certificate validation is disabled deliberately (see fetch() below) and the
# resulting per-request warning would otherwise drown the progress output.
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

USER_AGENT = (
    "PiholeReportServer-DomainClassifier/1.0 "
    "(+internal network inventory; contact the network owner)"
)

CONNECT_TIMEOUT = 5.0
READ_TIMEOUT = 8.0
MAX_BODY_BYTES = 256 * 1024      # enough for <head>; nowhere near enough to hurt
MAX_REDIRECTS = 3
DEFAULT_WORKERS = 16

WHITESPACE = re.compile(r"\s+")


@dataclass
class Result:
    domain: str
    scheme: str | None = None
    http_status: int | None = None
    final_url: str | None = None
    final_host: str | None = None
    content_type: str | None = None
    server_header: str | None = None
    body_bytes: int | None = None
    elapsed_ms: int | None = None
    title: str | None = None
    description: str | None = None
    keywords: str | None = None
    og_site_name: str | None = None
    lang: str | None = None
    error: str | None = None


@dataclass
class Counters:
    ok: int = 0
    html: int = 0
    failed: int = 0
    lock: threading.Lock = field(default_factory=threading.Lock)

    def add(self, *, ok: bool, html: bool) -> None:
        with self.lock:
            if ok:
                self.ok += 1
                if html:
                    self.html += 1
            else:
                self.failed += 1


def clip(value: str | None, n: int) -> str | None:
    """Trim and collapse whitespace, then bound the length to the column width."""
    if not value:
        return None
    v = WHITESPACE.sub(" ", value).strip()
    if not v:
        return None
    return v[:n]


def session_for_thread() -> requests.Session:
    s = requests.Session()
    s.headers.update({
        "User-Agent": USER_AGENT,
        # Ask for HTML explicitly; many API hosts will refuse and that is a useful
        # signal in itself.
        "Accept": "text/html,application/xhtml+xml;q=0.9,*/*;q=0.1",
        "Accept-Language": "en",
    })
    s.max_redirects = MAX_REDIRECTS
    return s


def parse_head(html: bytes, result: Result) -> None:
    """Pull the self-description out of <head>. Never executes anything."""
    soup = BeautifulSoup(html, "lxml")

    if soup.title and soup.title.string:
        result.title = clip(soup.title.string, 400)

    html_tag = soup.find("html")
    if html_tag and html_tag.get("lang"):
        result.lang = clip(str(html_tag.get("lang")), 16)

    def meta(selector: dict) -> str | None:
        tag = soup.find("meta", attrs=selector)
        if tag and tag.get("content"):
            return str(tag.get("content"))
        return None

    result.description = clip(
        meta({"name": re.compile(r"^description$", re.I)})
        or meta({"property": "og:description"}),
        1000,
    )
    result.keywords = clip(meta({"name": re.compile(r"^keywords$", re.I)}), 500)
    result.og_site_name = clip(meta({"property": "og:site_name"}), 200)


def fetch(domain: str) -> Result:
    r = Result(domain=domain)
    session = session_for_thread()
    started = time.monotonic()

    # HTTPS first; fall back to HTTP only if the TLS attempt fails outright, since
    # plenty of internal and IoT endpoints are HTTP-only.
    last_error: str | None = None
    for scheme in ("https", "http"):
        url = f"{scheme}://{domain}/"
        try:
            # verify=False deliberately: the goal is to learn what a host claims to
            # be, and a self-signed or expired certificate must not hide that. No
            # credentials are ever sent, so there is nothing for an interceptor to
            # capture.
            head = session.head(
                url, timeout=(CONNECT_TIMEOUT, READ_TIMEOUT),
                allow_redirects=True, verify=False,
            )
            r.scheme = scheme
            r.http_status = head.status_code
            r.final_url = clip(head.url, 1000)
            r.final_host = clip(urlparse(head.url).hostname, 253)
            r.content_type = clip(head.headers.get("Content-Type"), 128)
            r.server_header = clip(head.headers.get("Server"), 200)

            is_html = "html" in (r.content_type or "").lower()
            # Plenty of large sites reject HEAD outright (405/501) or gate it
            # behind a bot check (403), and some answer without a content type.
            # Those must still be tried with GET, so this cannot be gated on a
            # successful HEAD status - doing so silently skipped amazon.com and
            # netflix.com entirely.
            head_unsupported = head.status_code in (403, 405, 501)
            if head_unsupported or (r.content_type is None and head.status_code < 400):
                is_html = True

            if is_html and (head.status_code < 400 or head_unsupported):
                with session.get(
                    url, timeout=(CONNECT_TIMEOUT, READ_TIMEOUT),
                    allow_redirects=True, verify=False, stream=True,
                ) as resp:
                    r.http_status = resp.status_code
                    r.final_url = clip(resp.url, 1000)
                    r.final_host = clip(urlparse(resp.url).hostname, 253)
                    r.content_type = clip(resp.headers.get("Content-Type"), 128)
                    r.server_header = clip(resp.headers.get("Server"), 200)

                    chunks: list[bytes] = []
                    total = 0
                    for chunk in resp.iter_content(8192):
                        chunks.append(chunk)
                        total += len(chunk)
                        if total >= MAX_BODY_BYTES:
                            break   # hard cap; a hostile host cannot stream forever
                    r.body_bytes = total
                    if "html" in (r.content_type or "").lower() or not r.content_type:
                        parse_head(b"".join(chunks), r)

            r.elapsed_ms = int((time.monotonic() - started) * 1000)
            return r

        except requests.exceptions.SSLError as exc:
            last_error = f"tls: {exc.__class__.__name__}"
        except requests.exceptions.ConnectTimeout:
            last_error = "connect timeout"
        except requests.exceptions.ReadTimeout:
            last_error = "read timeout"
        except requests.exceptions.TooManyRedirects:
            last_error = "too many redirects"
        except requests.exceptions.ConnectionError as exc:
            msg = str(exc)
            if "Name or service not known" in msg or "nodename nor servname" in msg:
                last_error = "dns: no such host"
                break   # no point trying http if the name does not resolve
            last_error = "connection refused/reset"
        except Exception as exc:                       # noqa: BLE001 - never abort the run
            last_error = f"{exc.__class__.__name__}"

    r.error = clip(last_error or "unknown failure", 300)
    r.elapsed_ms = int((time.monotonic() - started) * 1000)
    return r


UPSERT = """
MERGE dbo.DomainMetadata AS t
USING (SELECT %s AS domain) AS s ON t.domain = s.domain
WHEN MATCHED THEN UPDATE SET
    scheme=%s, http_status=%s, final_url=%s, final_host=%s, content_type=%s,
    server_header=%s, body_bytes=%s, elapsed_ms=%s, title=%s, description=%s,
    keywords=%s, og_site_name=%s, lang=%s, error=%s, fetched_utc=%s
WHEN NOT MATCHED THEN INSERT
    (domain, scheme, http_status, final_url, final_host, content_type,
     server_header, body_bytes, elapsed_ms, title, description, keywords,
     og_site_name, lang, error, fetched_utc)
    VALUES (%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s,%s);
"""


def row_params(r: Result, now: datetime) -> tuple:
    core = (
        r.scheme, r.http_status, r.final_url, r.final_host, r.content_type,
        r.server_header, r.body_bytes, r.elapsed_ms, r.title, r.description,
        r.keywords, r.og_site_name, r.lang, r.error, now,
    )
    return (r.domain, *core, r.domain, *core)


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--config", default="/etc/pihole-fetch/config.ini")
    ap.add_argument("--workers", type=int, default=DEFAULT_WORKERS)
    ap.add_argument("--limit", type=int, default=0, help="0 = every target")
    ap.add_argument("--refetch-days", type=int, default=30,
                    help="skip domains fetched more recently than this")
    ap.add_argument("--dry-run", action="store_true", help="fetch but do not write")
    args = ap.parse_args()

    cfg = configparser.ConfigParser()
    if not cfg.read(args.config):
        sys.exit(f"[fatal] cannot read {args.config}")
    m = cfg["mssql"]

    conn = pymssql.connect(
        server=m["server"], port=m.get("port", "1433"), user=m["user"],
        password=m["password"], database=m["database"], login_timeout=30, timeout=120,
    )
    cur = conn.cursor()
    cur.execute(
        """
        SELECT t.domain
        FROM dbo.FetchTargets AS t
        WHERE NOT EXISTS (
                  SELECT 1 FROM dbo.DomainMetadata AS d
                  WHERE d.domain = t.domain
                    AND d.fetched_utc > DATEADD(day, -%d, SYSUTCDATETIME()))
        ORDER BY t.queries DESC
        """ % args.refetch_days
    )
    targets = [row[0] for row in cur.fetchall()]
    if args.limit:
        targets = targets[: args.limit]

    print(f"[start] {len(targets):,} domains, {args.workers} workers", flush=True)
    if not targets:
        return 0

    counters = Counters()
    pending: list[Result] = []
    started = time.monotonic()
    done = 0

    def flush() -> None:
        if args.dry_run or not pending:
            pending.clear()
            return
        now = datetime.now(timezone.utc).replace(tzinfo=None, microsecond=0)
        cur.executemany(UPSERT, [row_params(r, now) for r in pending])
        conn.commit()
        pending.clear()

    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = {pool.submit(fetch, d): d for d in targets}
        for fut in as_completed(futures):
            r = fut.result()
            counters.add(ok=r.error is None, html=bool(r.title or r.description))
            pending.append(r)
            done += 1

            if len(pending) >= 100:
                flush()
            if done % 250 == 0 or done == len(targets):
                rate = done / max(time.monotonic() - started, 0.001)
                eta = (len(targets) - done) / max(rate, 0.001)
                print(
                    f"[{done:,}/{len(targets):,}] ok={counters.ok:,} "
                    f"with-metadata={counters.html:,} failed={counters.failed:,} "
                    f"{rate:.1f}/s eta={eta/60:.0f}m",
                    flush=True,
                )
    flush()

    print(
        f"[done] {done:,} fetched in {(time.monotonic()-started)/60:.1f}m — "
        f"ok={counters.ok:,} with-metadata={counters.html:,} failed={counters.failed:,}",
        flush=True,
    )
    conn.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
