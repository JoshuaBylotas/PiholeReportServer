#!/usr/bin/env python3
"""Classify domains using the local LLM, given the metadata the site itself served.

This is pass 3 of the categoriser. Passes 1 and 2 (curated rules, then the UT1 and
Blocklist Project corpora) are deterministic and cover ~94.5% of query volume. This
handles what they cannot: the obscure tail, and a specific human-readable
description for everything.

The model is given evidence, not asked to guess. Where dbo.DomainMetadata has a
title and description fetched from the site, that goes in the prompt -- which is
the difference between "classify vesync.com" and "classify a site whose title is
'Vesync.com' and which describes itself as smart home devices". A failed fetch is
evidence too: no HTTP service on either port is a strong sign of a telemetry or
API endpoint rather than a web site.

Two rules the pipeline depends on:

  * A deterministic category is NEVER overwritten. If a rule or corpus decided it,
    the model may only add a description. Its output is the least trustworthy input
    here and must not displace the most trustworthy.
  * Everything the model writes is stored with source='model', so a report can
    exclude guesses entirely.

Domains are batched into one prompt because prompt evaluation is ~5x faster than
generation on this hardware; per-domain prompting would spend most of the time
re-reading the same instructions.
"""
from __future__ import annotations

import argparse
import configparser
import json
import sys
import time

import pymssql
import requests

CATEGORIES = [
    "advertising", "tracking", "analytics", "infrastructure", "cloud", "software",
    "streaming", "social", "shopping", "news", "gaming", "iot", "finance", "adult",
    "malware", "communication", "search", "education", "health", "travel",
    "government", "sports", "local", "work", "unknown",
]

SYSTEM = f"""You classify internet domains for a home-network DNS report.

For each domain you are given: the domain name, and where available the title and
description the site itself served, plus the deterministic category already
assigned by a curated rule or a categorised corpus.

Reply with ONLY a JSON object containing a "results" array, one entry per domain
supplied, in the same order. Return an entry for EVERY domain given:
{{"results":[{{"domain":"...","category":"...","subcategory":"...","description":"...","confidence":0-100}}]}}

category MUST be one of: {", ".join(CATEGORIES)}

Rules:
- If a deterministic category is supplied, KEEP it. You may refine subcategory and
  description, but do not change the category.
- subcategory: two or three words, specific. "video streaming CDN", "smart plug
  telemetry", "display ad exchange".
- description: ONE short factual sentence about what the domain is for.
- Base it on the supplied evidence. If there is no evidence and you do not
  recognise the domain, use category "unknown" and confidence below 40 rather
  than inventing something.
- A domain with no HTTP service is usually an API or telemetry endpoint, not a
  web site. Prefer "infrastructure", "iot" or "cloud" for those.
- Never invent a title or description that was not supplied."""


def build_prompt(rows) -> str:
    lines = []
    for r in rows:
        domain, title, desc, err, status, cat, sub = r
        parts = [f"domain: {domain}"]
        if cat:
            parts.append(f"already_categorised_as: {cat}" + (f" / {sub}" if sub else ""))
        if title:
            parts.append(f"title: {title[:160]}")
        if desc:
            parts.append(f"description: {desc[:240]}")
        if err:
            parts.append(f"fetch_failed: {err[:60]}")
        elif status and status >= 400:
            parts.append(f"http_status: {status}")
        lines.append(" | ".join(parts))
    return "\n".join(lines)


def call_model(endpoint: str, model: str, prompt: str, timeout: int) -> list[dict]:
    resp = requests.post(
        f"{endpoint.rstrip('/')}/api/generate",
        json={
            "model": model,
            "system": SYSTEM,
            "prompt": prompt,
            "stream": False,
            "format": "json",
            "options": {"temperature": 0.1, "num_predict": 2048},
        },
        timeout=timeout,
    )
    resp.raise_for_status()
    body = resp.json().get("response", "")

    # format:"json" constrains the grammar but the model may still wrap the array
    # in an object, so accept either shape rather than failing the whole batch.
    try:
        parsed = json.loads(body)
    except json.JSONDecodeError:
        return []
    if isinstance(parsed, dict):
        for key in ("domains", "results", "items", "data"):
            if isinstance(parsed.get(key), list):
                return parsed[key]
        return [parsed] if "domain" in parsed else []
    return parsed if isinstance(parsed, list) else []


# {limit} is substituted with .format(), not %, because the LIKE patterns below
# contain literal % wildcards that Python's % formatting would try to consume.
SELECT_WORK = """
SELECT TOP ({limit})
       m.domain, m.title, m.description, m.error, m.http_status,
       c.category, c.subcategory
FROM dbo.DomainMetadata AS m
     JOIN dbo.FetchTargets AS f ON f.domain = m.domain
     LEFT JOIN dbo.DomainCategory AS c ON c.domain = m.domain
WHERE (c.domain IS NULL                                       -- never categorised
       OR (c.description IS NULL AND c.source <> 'manual'))   -- or lacking a description
  -- DNS artefacts, not web sites: SRV records, wildcards, the bare root.
  -- LEFT() rather than LIKE: the underscore is a LIKE wildcard, and escaping
  -- it through pymssql to SQL Server is more fragile than just not using it.
  AND LEFT(m.domain, 1) <> '_'
  AND LEFT(m.domain, 1) <> '*'
  AND m.domain <> '.'
  AND CHARINDEX('.', m.domain) > 0
-- Most-queried first, so an interrupted run has still done the work that matters.
ORDER BY f.queries DESC;
"""

UPSERT = """
MERGE dbo.DomainCategory AS t
USING (SELECT %s AS domain) AS s ON t.domain = s.domain
WHEN MATCHED AND t.source <> 'manual' THEN
    UPDATE SET subcategory   = COALESCE(%s, t.subcategory),
               description   = %s,
               -- A deterministic category is never displaced by a guess; only a
               -- row the model itself created may have its category changed.
               category      = CASE WHEN t.source = 'model' THEN %s ELSE t.category END,
               confidence    = CASE WHEN t.source = 'model' THEN %s ELSE t.confidence END,
               classified_utc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (domain, category, subcategory, description, source, confidence, classified_utc)
    VALUES (%s, %s, %s, %s, 'model', %s, SYSUTCDATETIME());
"""


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--config", default="/etc/pihole-fetch/config.ini")
    ap.add_argument("--endpoint", default="http://10.20.0.14:11434")
    ap.add_argument("--model", default="qwen2.5:7b-instruct")
    ap.add_argument("--batch", type=int, default=15)
    ap.add_argument("--limit", type=int, default=0, help="0 = everything outstanding")
    ap.add_argument("--timeout", type=int, default=300)
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    cfg = configparser.ConfigParser()
    if not cfg.read(args.config):
        sys.exit(f"[fatal] cannot read {args.config}")
    m = cfg["mssql"]

    conn = pymssql.connect(
        server=m["server"], port=m.get("port", "1433"), user=m["user"],
        password=m["password"], database=m["database"], login_timeout=30, timeout=300,
    )
    cur = conn.cursor()
    cur.execute(SELECT_WORK.format(limit=args.limit if args.limit else 1000000))
    work = cur.fetchall()

    print(f"[start] {len(work):,} domains to classify, batch={args.batch}, model={args.model}",
          flush=True)
    if not work:
        return 0

    started = time.monotonic()
    written = 0
    unparsed = 0

    for i in range(0, len(work), args.batch):
        batch = work[i:i + args.batch]
        by_domain = {r[0]: r for r in batch}

        # Retry with backoff rather than skipping. A restart of the inference
        # service used to fail every remaining batch in seconds and burn through
        # the whole work list: 194 batches were lost that way, because a transient
        # outage was treated as a permanent per-batch failure.
        out = None
        for attempt in range(1, 6):
            try:
                out = call_model(args.endpoint, args.model, build_prompt(batch), args.timeout)
                break
            except requests.exceptions.RequestException as exc:
                wait = min(60, 5 * attempt)
                print(f"[warn] batch at {i} attempt {attempt}/5: "
                      f"{exc.__class__.__name__}; retrying in {wait}s", flush=True)
                time.sleep(wait)
            except Exception as exc:                        # noqa: BLE001
                print(f"[warn] batch at {i} failed: {exc.__class__.__name__}: {exc}", flush=True)
                break
        if out is None:
            print(f"[warn] batch at {i} abandoned after 5 attempts", flush=True)
            continue

        rows = []
        for item in out:
            if not isinstance(item, dict):
                continue
            domain = str(item.get("domain", "")).strip().lower()
            # Only accept answers for domains actually asked about: the model
            # occasionally invents extra entries.
            if domain not in by_domain:
                continue
            cat = str(item.get("category", "unknown")).strip().lower()
            if cat not in CATEGORIES:
                cat = "unknown"
            sub = (str(item.get("subcategory", "")).strip() or None)
            desc = (str(item.get("description", "")).strip() or None)
            try:
                conf = max(0, min(100, int(item.get("confidence", 50))))
            except (TypeError, ValueError):
                conf = 50
            rows.append((domain, sub and sub[:64], desc and desc[:400], cat, conf,
                         domain, cat, sub and sub[:64], desc and desc[:400], conf))

        if len(rows) < len(batch):
            unparsed += len(batch) - len(rows)

        if rows and not args.dry_run:
            cur.executemany(UPSERT, rows)
            conn.commit()
        written += len(rows)

        done = min(i + args.batch, len(work))
        rate = done / max(time.monotonic() - started, 0.001)
        print(f"[{done:,}/{len(work):,}] written={written:,} unmatched={unparsed:,} "
              f"{rate:.1f}/s eta={(len(work)-done)/max(rate,0.001)/60:.0f}m", flush=True)

    print(f"[done] {written:,} classified in {(time.monotonic()-started)/60:.1f}m "
          f"({unparsed:,} not returned by the model)", flush=True)
    conn.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
