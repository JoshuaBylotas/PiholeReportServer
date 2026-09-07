# Architecture

## The whole pipeline

```
┌──────────────────────┐
│  Pi-hole host        │   Raspberry Pi / Debian, Pi-hole v6
│  <PIHOLE_IP>         │
│                      │
│  pihole-FTL.db       │   SQLite + WAL, one row per DNS query
│        │             │
│        │ read-only   │
│        ▼             │
│  pihole-sqlsync      │   systemd service, Python + pymssql
│  /opt/pihole-sqlsync │   tails the `queries` view, watermark = MAX(id)
└──────────┬───────────┘
           │  TDS 1433, batched INSERTs
           ▼
┌──────────────────────┐
│  SQL Server          │   <SQL_HOST>, database <SQL_DB>
│                      │
│  dbo.PiholeQueries   │   fact table, PK = FTL's own id
│  dbo.DimClient       │   dimensions, refreshed daily by dims.py
│  dbo.DimType         │
│  dbo.DimStatus       │
│  dbo.Adlists         │
│  dbo.GravityDomains  │
└──────────┬───────────┘
           │  read-only login (db_datareader)
           ▼
┌──────────────────────┐         ┌─────────────────────┐
│  Pi-hole Report      │◄───────►│  Microsoft Entra ID │
│  Server              │  OIDC   │  <TENANT_ID>        │
│  ASP.NET Core 10     │         └─────────────────────┘
│                      │
│  /            overview
│  /Reports     pre-canned library
│  /Query/Builder  guided builder
│  /Query/Sql   raw SQL (role-gated)
│  /Diagnostics health
│  /healthz     anonymous liveness
└──────────────────────┘
```

## Why the warehouse exists at all

Pi-hole's own database rotates. Its web UI is excellent for "what happened in the last
day or two" and useless for "how has this device behaved since April". Streaming every
row into SQL Server buys three things:

1. **Retention** independent of Pi-hole's own settings.
2. **Joins** against dimension tables — host names, MAC vendors, blocklist membership —
   that Pi-hole does not expose in a queryable form.
3. **Real SQL**, so a question that nobody anticipated can still be answered.

## Request flow, and where authorization happens

Every request passes through the same gate:

1. `UseAuthentication` resolves the session cookie into a `ClaimsPrincipal`.
2. `UseAuthorization` applies the **fallback policy**, set in `Program.cs` to the
   `Viewer` policy. This is the mechanism that makes the *whole* site private: a page
   does not have to remember to protect itself. Only `[AllowAnonymous]` endpoints
   (`/healthz`, `/Error`, and the sign-in callbacks) are exempt.
3. Anonymous users are challenged, which redirects to Entra ID.
4. `/Query/Sql` carries an additional `[Authorize(Policy = SqlAuthor)]`, so viewing the
   report library and running arbitrary SQL are separate privileges.

```
Request ──► Authentication ──► Authorization (fallback = Viewer)
                                     │
                    ┌────────────────┴─────────────────┐
                    ▼                                  ▼
             authenticated                       anonymous
                    │                                  │
        ┌───────────┴──────────┐              redirect to Entra ID
        ▼                      ▼
   Viewer pages          /Query/Sql
                     requires SqlAuthor role
```

## Two ways to run a custom report

This is the most important design decision in the application, because the two paths
have very different risk profiles.

### The guided builder — safe for everyone

`BuilderSqlComposer` maps an enum to a SQL fragment from a fixed dictionary. A user
picks *Group by → Domain*; the composer looks up `q.domain`. There is no path by which
caller text becomes SQL. Free-text filters ("domain contains…") travel as **bound
parameters**, and their `LIKE` wildcards are escaped so `100%` matches a literal
`100%`.

Because of that, the builder needs no allowlist of users beyond "can sign in".

### The SQL console — role-gated, defence in depth

Free text is inherently harder. Three independent layers apply:

1. **`SqlGuard`** — strips comments, string literals and bracketed identifiers, then
   requires the statement to begin with `SELECT` or `WITH`, rejects stacked statements,
   and rejects a denylist of verbs (`DELETE`, `DROP`, `EXEC`, `INTO`, `OPENROWSET`,
   `WAITFOR`, …). Scrubbing first is what stops `WHERE domain = 'delete-me.com'` from
   being mistaken for DML, and stops `/* SELECT */ DELETE …` from sneaking past.
2. **A read-only SQL login.** The application connects as a principal with nothing but
   `db_datareader`. Even a statement that defeated `SqlGuard` would be refused by the
   database. This, not the regex, is the real control.
3. **Row cap and statement timeout.** `ReportRunner` stops reading at
   `Reporting:MaxRows` and sets `CommandTimeout`, so a careless cross join cannot pin
   the server.

The row cap is applied **while reading the result**, not by rewriting the user's SQL.
Rewriting is unreliable: a CTE cannot simply be wrapped in a derived table, and
appending `TOP` collides with an existing one. Stopping the reader is exact and works
for any query shape.

## The data model

`dbo.PiholeQueries` is the fact table — one row per DNS query, keyed on Pi-hole FTL's
own `id`, which is what makes the loader idempotent.

| Column | Type | Notes |
|--------|------|-------|
| `id` | `bigint` PK | FTL's `query_storage.id`; the loader's watermark |
| `ts` | `datetime2` | **UTC.** Converted from FTL's unix timestamp |
| `type` | `int` | DNS record type; join `DimType` |
| `status` | `int` | How it was answered; join `DimStatus` |
| `status_text` | `varchar` | Denormalised label, written at load time |
| `domain` | `varchar(255)` | See the note on encoding below |
| `client` | `varchar(255)` | Client IP; join `DimClient.ip` |
| `forward` | `varchar(255)` | Upstream resolver, null for cache hits and blocks |
| `reply_type` | `int` | |
| `reply_time` | `float` | **Seconds**, not milliseconds |
| `dnssec` | `int` | |
| `ede` | `int` | Extended DNS error |

Dimensions, refreshed on a daily timer by `dims.py`:

| Table | Grain | Join |
|-------|-------|------|
| `dbo.DimClient` | one row per known **IP** (`ip`, `name`, `mac`, `mac_vendor`, `interface`, `num_queries`, `last_query`, `reported_name`, `name_ambiguous`) | `DimClient.ip = PiholeQueries.client` |
| `dbo.DimType` | one row per record type | `DimType.type = PiholeQueries.type` |
| `dbo.DimStatus` | one row per status code | `DimStatus.status = PiholeQueries.status` |
| `dbo.Adlists` | one row per subscribed blocklist | `Adlists.id = GravityDomains.adlist_id` |
| `dbo.GravityDomains` | domain → blocklist bridge | `GravityDomains.domain = PiholeQueries.domain` |

### Two traps in `DimClient`

**The grain is the IP, not the device.** A phone with an IPv4 lease and several
IPv6 addresses has several rows. One host here has six. Anything counting
"devices" by counting rows, or grouping by `ip`, is wrong; group by `mac`.

**`num_queries` must never be summed.** FTL stores it per device and `dims.py`
copies it onto each of that device's IP rows, so `SUM(num_queries)` multiplies
the total by the number of addresses. Across this warehouse the sum over rows is
175,755,074 against a correct per-device total of 76,585,199 — 52 devices are
affected. Use `MAX(num_queries)` grouped by `mac`.

It is also a **lifetime** counter carried over from FTL and survives the rotation
of FTL's own database, so it is much larger than anything derived from
`PiholeQueries` (76.5M against 24.8M rows streamed) and the two are not
comparable. For query counts, count `PiholeQueries`.

`name_ambiguous = 1` marks a row whose reverse-DNS name was returned for more
than one device; `reported_name` keeps what FTL originally said. 109 of 220 rows
are currently flagged, almost all of it one stale PTR record that resolves 37
addresses across 34 devices to the same name. Reports should group by `mac` or
exclude flagged rows rather than trust `name`.

### Status codes worth knowing

`status` drives most reporting logic:

| Codes | Meaning |
|-------|---------|
| `2` | Forwarded upstream |
| `3`, `17` | Answered from cache (17 = stale cache) |
| `1`, `4`, `5`, `9`, `10`, `11` | Blocked — gravity, regex, denylist, or a CNAME thereof |
| `12`, `13` | Retried |
| `14` | In progress |

Reports that ask "what would have been blocked" therefore exclude the already-blocked
statuses, so the answer reflects traffic that genuinely got through.

### Two encoding traps

Both of these are load-bearing for the reports, and both cost real debugging time:

1. **`domain` is `varchar` under `SQL_Latin1_General_CP1_CI_AS`.** Pi-hole occasionally
   logs a domain containing bytes that are not valid UTF-8. The loader decodes with
   `errors="replace"`, producing `U+FFFD` — which CP1252 cannot represent, so it lands
   in the column as a literal `?`. The `malformed-domains` report finds these.

2. **Never search for that character with an `nvarchar` pattern.**
   `domain LIKE N'%' + NCHAR(65533) + N'%'` matches **every row in the table**, because
   `U+FFFD` is collation-ignorable when an `nvarchar` pattern is compared against a
   `varchar` column. Use `CHARINDEX('?', domain) > 0`, or force a `_BIN2` collation.

### Timestamps

`ts` is UTC throughout — the loader converts FTL's unix timestamps with
`datetime.timezone.utc` and strips the offset. The UI labels date inputs "UTC" rather
than silently converting, so a report range means the same thing to everyone.

If timestamps ever look shifted, check the clock on the *reporting workstation* before
suspecting the pipeline. A desktop whose clock has drifted will make correct data look
stale.

## Project layout

```
PiholeReportServer/
├─ src/PiholeReportServer/
│  ├─ Program.cs                  auth, policies, DI, middleware order
│  ├─ Configuration/              strongly-typed options + role/policy names
│  ├─ Data/SqlConnectionFactory   the four SQL authentication modes
│  ├─ Services/
│  │  ├─ SqlGuard                 read-only statement screening
│  │  ├─ BuilderSqlComposer       enum → SQL fragment, parameters for text
│  │  ├─ ReportCatalog            loads reports.json + .sql, hot-reloads
│  │  ├─ ReportRunner             parameter binding, row cap, timeout
│  │  └─ CsvExporter
│  ├─ Reports/                    reports.json + one .sql per report
│  ├─ Pages/                      Razor Pages
│  └─ wwwroot/css/theme.css       Pinecrest design tokens
├─ tests/PiholeReportServer.Tests/
├─ docs/                          public documentation (placeholders only)
└─ private/                       real environment values — gitignored
```

Report definitions are deployed as **content**, not compiled in, so a new report is a
`.sql` file plus a manifest entry — no rebuild, and `ReportCatalog` notices the change
on the next request.
