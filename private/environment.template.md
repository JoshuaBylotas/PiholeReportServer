# Environment — <SITE NAME>

> Copy to `private/environment.md` and fill in. That copy is gitignored.
> **Record no passwords, secrets or keys here** — only where they live.

_Last verified: <YYYY-MM-DD>_

## Hosts

| Role | Host name | Address | Notes |
|------|-----------|---------|-------|
| Pi-hole / DNS | | | Pi-hole version, OS, SSH user |
| SQL Server | | | Version and edition |
| Report server | | | Where the site runs, TLS certificate source |

## Entra ID

| Item | Value |
|------|-------|
| Tenant name | |
| Tenant ID | |
| App registration name | |
| Application (client) ID | |
| Redirect URIs registered | |
| Credential type | secret / certificate |
| Credential **expires** | ← calendar reminder |
| `RequireAppRoles` | true / false |

### Role assignments

| Principal | Role |
|-----------|------|
| | `Report.Viewer` |
| | `Report.SqlAuthor` |

## SQL Server

| Item | Value |
|------|-------|
| Instance | |
| Version / edition | |
| Database | |
| `Sql:AuthMode` in use | |
| Entra auth possible? | see docs/03 — needs Azure SQL, MI, or Arc-enabled 2022+ |
| Report login (read-only) | |
| Loader login (read/write) | |
| Encrypt / TrustServerCertificate | |

### Where the credentials actually live

| Credential | Location |
|------------|----------|
| `AzureAd:ClientSecret` | e.g. `AzureAd__ClientSecret` env var on <host> |
| `Sql:Password` (report) | e.g. user-secrets on dev, `Sql__Password` env var on <host> |
| Loader password | `/etc/pihole-sqlsync/config.ini` on the Pi-hole host, mode 600 |

## Pipeline

| Item | Value |
|------|-------|
| Loader path | `/opt/pihole-sqlsync/sync.py` |
| Config path | `/etc/pihole-sqlsync/config.ini` (mode 600) |
| Service | `pihole-sqlsync.service` |
| Dimension timer | `pihole-sqlsync-dims.timer` |
| Poll interval | |
| FTL `DBinterval` | |
| Pi-hole blocking active? | affects what the would-be-blocked reports mean |

## Warehouse baseline

Record after the initial backfill, so drift is detectable later.

| Metric | Value | As of |
|--------|-------|-------|
| Rows in `dbo.PiholeQueries` | | |
| `MIN(ts)` / `MAX(ts)` | | |
| `MAX(id)` | | |
| Indexes built? | | |
| `dims.py` last run | | |

## Site-specific quirks

Anything that would waste an hour if forgotten — duplicate IPs, stale DNS records,
clients bypassing Pi-hole, clock skew on a particular machine.

| Quirk | Impact | Status |
|-------|--------|--------|
| | | |
