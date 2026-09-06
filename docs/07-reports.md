# Reports

## The pre-canned library

Fifteen reports ship with the application, grouped by category. Each opens with a
sensible default date range you can change before running, and any result can be
exported to CSV.

### Traffic

| Report | Answers |
|--------|---------|
| **Top domains** | What is being asked for most, how many distinct clients want it, and whether it is on a blocklist |
| **Query volume by day** | Daily totals with cache-hit and blocklist-match shares — the first place to look for a trend or an outage |
| **Query volume by hour of day** | When the network is busiest, averaged across the window so a short range is not skewed by day count |
| **Query types** | A / AAAA / HTTPS / PTR breakdown, resolved through `DimType` |
| **Status breakdown** | Forwarded vs cached vs blocked, as counts and percentages |

### Clients

| Report | Answers |
|--------|---------|
| **Top clients** | Busiest devices, resolved to host name, MAC and vendor |
| **Single client detail** | Every domain one device asked for — takes an exact client IP |
| **Newly seen domains** | Domains whose *first ever* appearance falls in the window. Good for spotting a new service, or beaconing |

### Blocking

| Report | Answers |
|--------|---------|
| **Would-be-blocked domains** | Queries that were answered but whose domain is on a blocklist. **The** report when Pi-hole is in log-only mode |
| **Blocklist effectiveness** | Which subscribed list would catch the most traffic, and how much of each list is dead weight |
| **Would-be-blocked by client** | Which devices generate the most blocklist-matching traffic, as a share of their own total |

### Performance

| Report | Answers |
|--------|---------|
| **Upstream resolvers** | Where forwarded queries went, with average / min / p95 / max reply time |
| **Slowest domains** | Worst average upstream latency, filtered to domains with enough samples to mean anything |

### Data quality

| Report | Answers |
|--------|---------|
| **Malformed domain names** | Rows whose domain is not a valid host name, classified by defect |
| **Ingest freshness** | How current the warehouse is, and whether the loader left gaps in the last week |

---

## Notes on the trickier ones

### Newly seen domains

The "first seen" subquery scans the **whole** history, not just the window. That is
deliberate: a domain that appeared once last year is not new because it reappeared
today. It is the most expensive report in the library for exactly that reason.

### Would-be-blocked

Excludes statuses `1, 4, 5, 9, 10, 11` — the already-blocked ones — so the result
reflects traffic that genuinely got through. With Pi-hole blocking *enabled* this
report is nearly empty, which is the correct answer.

### Malformed domain names

Classifies each defect:

| Defect | Cause |
|--------|-------|
| `substituted byte` | The source row had non-UTF-8 bytes; the loader substituted `U+FFFD`, which lands as `?` in the `varchar` column |
| `embedded port` | A client sent something like `host:443` as a query name |
| `whitespace` | Malformed query from a misbehaving client |
| `empty label` | A `..` sequence in the name |

The `substituted byte` rows are the ones tied to the loader's decode handling — see
[the bad-row failure mode](05-pihole-sqlsync.md#the-bad-row-failure-mode--read-this-before-you-trust-restartalways).

> Do **not** try to find these with `domain LIKE N'%' + NCHAR(65533) + N'%'`. Against a
> `varchar` column that matches **every row**, because `U+FFFD` is collation-ignorable
> in a mixed-type comparison. The report uses `CHARINDEX('?', domain) > 0`.

### Upstream resolvers

`PERCENTILE_CONT` in SQL Server is an analytic function only — there is no aggregate
form — so it cannot appear beside a `GROUP BY` over the same rows. The report computes
the p95 in its own CTE and joins it to the grouped aggregates. If you write your own
percentile report, expect this.

Also: `reply_time` is stored in **seconds**. Every report multiplies by 1000 to present
milliseconds.

---

## The guided builder

`/Query/Builder` composes a query from fixed choices — no SQL knowledge, and safe for
any signed-in user.

| Control | Options |
|---------|---------|
| **Group by** / **Then by** | Domain, Client, Client hostname, Query type, Status, Upstream, **Blocklist**, Hour, Day, Week |
| **Metric** | Query count, Distinct domains, Distinct clients, Avg reply ms, Max reply ms |
| **Filters** | Date range, domain contains, client IP contains, **clients** (multi-select), **blocklists** (multi-select), status code, query type |
| **Sort / Limit** | Ascending or descending; row limit clamped to `Reporting:MaxRows` |

Expand **Show the generated SQL** to see what it built — useful as a starting point for
the SQL console.

Two safety properties worth knowing, because they are what make this page unrestricted:

- Every SQL fragment comes from a fixed dictionary keyed by an enum. Caller text never
  becomes SQL.
- Free-text filters are **bound parameters**, and their `LIKE` wildcards are escaped, so
  a filter of `100%` matches a literal `100%` rather than acting as a pattern.

Joins are added only when the chosen dimensions need them, so the common "top domains"
shape stays a single-table scan.

### The two multi-selects

**Clients** lists devices from `dbo.DimClient`. The options show the device name but
their *values* are IPs, because that is what the fact table stores, what is indexed, and
because one host name can cover several addresses.

**Blocklists** lists the subscribed lists from `dbo.Adlists`, labelled by their comment
and domain count. Three states:

| Selection | Meaning |
|-----------|---------|
| nothing | No blocklist filter |
| `(any blocklist)` | On any subscribed list — what the old checkbox meant |
| one or more lists | Only traffic those specific lists would have caught |

`(any blocklist)` takes precedence over specific selections, because selecting it is a
*wider* request and must not be silently narrowed.

### Attributing traffic to a list

Pair the blocklist filter with **Group by → Blocklist** to see which list catches what.

One caveat the page states in-line: `dbo.GravityDomains` holds one row per
(domain, adlist) pair, so grouping by blocklist **counts each query once per matching
list**. A domain on three lists contributes to all three. Each per-list total is correct,
but their sum exceeds the total query count. That fan-out is precisely what makes the
question answerable — it is not an error.

The *filter* does not fan out: it uses `EXISTS` with its own alias, so filtering by list
never inflates counts.

---

## Working with a result set

Every result — pre-canned report, builder or SQL console — renders through the same
component, so these behave identically everywhere.

### Search

A search box above the table filters the rows **already fetched**. Space-separated terms
must *all* appear somewhere in the row, so `samsung advert` narrows rather than widening.
Escape clears it.

It is client-side by design: the rows are already materialised and row-capped by the
server, so filtering costs nothing and returns instantly. The corollary is that search
cannot find rows beyond the row cap — if a result is truncated, narrow the query or export
to CSV instead. The page says so when that applies.

### Paging

50 / 100 / 250 / 500 / All rows per page, with prev-next and a
"Showing 1–100 of 4,214 (filtered from 5,000)" readout.

Paging is client-side for the same reason: turning a page server-side would re-run an
aggregate over tens of millions of rows to fetch the next fifty.

Both controls stay hidden until the script initialises, so with JavaScript disabled the
full table renders rather than dead widgets.

### While a query runs

Submitting a query raises a full-page overlay with a spinner. It covers the viewport, so
it captures every click — that is what stops a second query being launched, or a nav link
being followed, mid-flight. Submit buttons are disabled alongside it, and the header is
dimmed and made inert.

Three cases are handled differently, because they end differently:

| Action | Overlay |
|--------|---------|
| Run a query | Clears when the new page renders |
| Export CSV | A download never navigates, so nothing would clear the overlay — it times itself out |
| Delete a saved report | Redirects, and clears on the new page |

Cancelling a delete confirmation does not raise the overlay at all, and returning via the
browser's back button clears it — otherwise the back-forward cache would restore a page
frozen behind a spinner that has nothing left to wait for.

## Saved reports

Anything you build in the builder, or write in the SQL console, can be saved to your own
account and re-run later. Saved reports appear on the **Reports** index alongside the
pre-canned library, and on the page that created them.

| | |
|---|---|
| Where | `dbo.SavedReports` — see [SQL Server setup](03-sql-server-setup.md#saved-reports-table) |
| Scope | **Yours only.** Every query filters on your Entra object id |
| Naming | Saving under an existing name **replaces** it |
| Limit | 200 per user |

Two details worth knowing:

- **Ownership is keyed on the Entra `oid` claim, not the UPN.** An `oid` is immutable, so
  renaming an account does not orphan its saved reports.
- **Builder specs store enum values by name, not ordinal.** An ordinal would silently
  re-point at a different column the moment a new dimension was inserted mid-enum, and
  every saved report would quietly start reporting on something else.

A saved SQL query is screened by `SqlGuard` both when saved and again on every run — so a
query stored before a schema change fails cleanly rather than silently returning the
wrong thing.

---

## The SQL console

`/Query/Sql` requires the `Report.SqlAuthor` role. One `SELECT` (or `WITH`) statement,
read-only, row-capped.

Rejected outright: anything not starting with `SELECT`/`WITH`, stacked statements, DML
and DDL verbs, `INTO`, `EXEC`, `OPENROWSET`/`OPENQUERY`, `WAITFOR`, system and extended
stored procedures, and four-part linked-server names.

The screening runs against SQL that has had comments, string literals and bracketed
identifiers stripped — so `WHERE domain = 'delete-me.example.com'` is fine, while
`/* SELECT */ DELETE FROM …` is not.

The real control is not the screening. It is that the application connects with a login
holding nothing but `db_datareader`; a statement that defeated the screening would
still be refused by SQL Server.

### Row cap behaviour

The cap is applied **while reading the result**, not by rewriting your SQL. Your query
runs as written; the reader simply stops at `Reporting:MaxRows` and the result is
flagged as truncated. Add your own `TOP (n)` to keep the server-side cost down too —
the cap limits what is transferred, not what SQL Server computes.

---

## Adding your own report

Two files, no rebuild, no redeploy.

### 1. Write the SQL

`src/PiholeReportServer/Reports/busiest-hour-per-client.sql`:

```sql
-- Each client's single busiest hour in the window.
WITH per_hour AS (
    SELECT q.client,
           DATEADD(hour, DATEDIFF(hour, 0, q.ts), 0) AS hour_bucket,
           COUNT_BIG(*) AS queries
    FROM dbo.PiholeQueries AS q
    WHERE q.ts >= @from
      AND q.ts <  @to
    GROUP BY q.client, DATEADD(hour, DATEDIFF(hour, 0, q.ts), 0)
),
ranked AS (
    SELECT *, ROW_NUMBER() OVER (PARTITION BY client ORDER BY queries DESC) AS rn
    FROM per_hour
)
SELECT TOP (@top)
       r.client                            AS ip,
       COALESCE(dc.name, '(unknown)')      AS hostname,
       r.hour_bucket,
       r.queries
FROM ranked AS r
     LEFT JOIN dbo.DimClient AS dc ON dc.ip = r.client
WHERE r.rn = 1
ORDER BY r.queries DESC;
```

### 2. Declare it

Add an entry to `Reports/reports.json`:

```jsonc
{
  "id": "busiest-hour-per-client",
  "title": "Busiest hour per client",
  "summary": "Each device's single heaviest hour in the window.",
  "category": "Clients",
  "order": 40,
  "parameters": [
    { "name": "from", "label": "From", "kind": "DateTime", "default": "-7d" },
    { "name": "to",   "label": "To",   "kind": "DateTime", "default": "now" },
    { "name": "top",  "label": "Rows", "kind": "Int",      "default": "100" }
  ]
}
```

`id` **must** match the `.sql` filename without its extension.

### Rules

- Every `@parameter` in the SQL must be declared, or the query fails with *"must
  declare the scalar variable"*. Declared-but-unused is harmless.
- Parameter kinds: `Date`, `DateTime`, `Int`, `Text`, `Choice` (with `choices`).
- Defaults accept relative forms — `today`, `now`, `-7d`, `-24h`, `+1d` — resolved when
  the page opens.
- `"required": false` renders an "(any)" option and binds `NULL`; write the SQL to
  cope, e.g. `AND (@client IS NULL OR q.client = @client)`.
- A report with no required parameters runs immediately on open.

`ReportCatalog` watches `reports.json`'s modification time and reloads on the next
request, so on a deployed server you can edit `Reports/` in place and refresh the page.

### Performance

Always filter on `ts` first — it is the indexed column every report leans on. Before
publishing something that will be run often:

```sql
SET STATISTICS IO, TIME ON;
-- your query with representative parameter values
```

Watch for a scan of `PiholeQueries` where you expected a seek. With tens of millions of
rows, an unfiltered `GROUP BY domain` is a minute of CPU, not a second.
