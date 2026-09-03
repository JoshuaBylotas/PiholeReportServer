# Pi-hole Report Server

A web front end for reporting over a Pi-hole DNS query warehouse in SQL Server.
Pre-canned reports, a point-and-click report builder, and a guarded SQL console —
with the entire site behind Microsoft Entra ID.

Pi-hole's own dashboard answers "what happened today". This answers "how has that
device behaved since April", by streaming every query into SQL Server and giving you
real SQL over the result.

```
Pi-hole (FTL/SQLite) ──► pihole-sqlsync ──► SQL Server ──► Pi-hole Report Server
                          (systemd, Python)   (warehouse)    (ASP.NET Core + Entra ID)
```

## Features

- **Entra ID on every page.** Enforced with an authorization *fallback policy*, so a
  page cannot forget to protect itself. Only `/healthz` is anonymous.
- **Two app roles.** `Report.Viewer` reads reports; `Report.SqlAuthor` additionally
  gets the SQL console.
- **15 pre-canned reports** across traffic, clients, blocking, performance and data
  quality — defined as plain `.sql` files plus a JSON manifest, hot-reloaded without a
  rebuild.
- **A guided builder** that is safe for any signed-in user: every SQL fragment comes
  from a fixed dictionary keyed by an enum, and free-text filters travel as bound
  parameters with `LIKE` wildcards escaped.
- **A guarded SQL console** — `SELECT`-only screening over comment- and literal-stripped
  SQL, a `db_datareader`-only login, a row cap and a statement timeout.
- **CSV export** from every result.
- **Four SQL authentication modes**, from a plain read-only login through to Entra
  on-behalf-of, so the database can see the real user where the platform supports it.
- **Pinecrest-styled UI** — pine green and warm gold on cream, Fraunces over Inter.

## Requirements

| | |
|---|---|
| .NET | 10.0 SDK (runtime only for deployment) |
| SQL Server | 2016+ (2022 recommended). Express is fine |
| Pi-hole | v6 |
| Entra ID | A tenant you can register an application in |

## Getting started

```powershell
git clone https://github.com/JoshuaBylotas/PiholeReportServer.git
cd PiholeReportServer

# Entra ID — see docs/02-entra-id-setup.md
dotnet user-secrets --project src/PiholeReportServer set "AzureAd:TenantId"     "<TENANT_ID>"
dotnet user-secrets --project src/PiholeReportServer set "AzureAd:ClientId"     "<CLIENT_ID>"
dotnet user-secrets --project src/PiholeReportServer set "AzureAd:ClientSecret" "<CLIENT_SECRET>"

# SQL Server — see docs/03-sql-server-setup.md
dotnet user-secrets --project src/PiholeReportServer set "Sql:Server"   "<SQL_HOST>"
dotnet user-secrets --project src/PiholeReportServer set "Sql:UserId"   "pihole_report_ro"
dotnet user-secrets --project src/PiholeReportServer set "Sql:Password" "<PASSWORD>"

dotnet run --project src/PiholeReportServer
```

Then open <https://localhost:7443>. You should be redirected straight to a Microsoft
sign-in page. **/Diagnostics** is the fastest way to confirm everything lined up — it
shows your claims, the effective SQL identity, and a present/missing inventory of the
warehouse tables.

## Documentation

Full setup lives in [`docs/`](docs/README.md):

| | |
|---|---|
| [Architecture](docs/01-architecture.md) | How it fits together, the data model, and the two custom-query paths |
| [Entra ID setup](docs/02-entra-id-setup.md) | App registration, redirect URIs, credentials, app roles, assignment |
| [SQL Server setup](docs/03-sql-server-setup.md) | Schema, indexes, the read-only login, and which auth modes your server can support |
| [Pi-hole setup](docs/04-pihole-setup.md) | Preparing the Pi-hole host |
| [The sync pipeline](docs/05-pihole-sqlsync.md) | Installing and operating the loader |
| [Deployment](docs/06-deployment.md) | IIS, Windows Service, or Docker |
| [Reports](docs/07-reports.md) | The library, and how to add your own |
| [Troubleshooting](docs/08-troubleshooting.md) | Symptoms → causes → fixes |

## A note on Entra ID and SQL Server

The application is fully gated by Entra ID. Whether the *database* also sees the
signed-in user depends on your SQL platform:

| Platform | Entra auth to SQL |
|----------|-------------------|
| Azure SQL Database / Managed Instance | Yes |
| SQL Server 2022+ Azure Arc-enabled, Entra auth enabled | Yes |
| SQL Server stand-alone, any version, incl. **Express** | **No** |

On a platform that cannot accept Entra tokens, the app uses a dedicated read-only SQL
login and enforces per-user access at the application layer instead. That is the
default and it is the right call for a stand-alone instance —
[the details are here](docs/03-sql-server-setup.md#can-my-sql-server-actually-accept-entra-tokens).

## Repository layout

```
src/PiholeReportServer/     the web application
  Services/SqlGuard         read-only statement screening
  Services/BuilderSqlComposer   enum → SQL fragment
  Reports/                  reports.json + one .sql per report
tests/                      xUnit — guard and composer behaviour
docs/                       public documentation, placeholders only
private/                    real environment values — gitignored
```

> **This repository is public.** Everything in `docs/` uses placeholders such as
> `<SQL_HOST>` and `<TENANT_ID>`. Real host names, addresses and identifiers belong in
> [`private/`](private/README.md), which `.gitignore` excludes. Passwords and secrets go
> in `dotnet user-secrets` or environment variables — never in the repository, ignored
> or not.

## Development

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
```

`Directory.Build.props` sets `TreatWarningsAsErrors`, which includes NuGet vulnerability
audit findings — a package with a known CVE fails the build rather than shipping.

## Security posture

- Site-wide Entra ID gate via an authorization fallback policy; `/healthz` is the sole
  anonymous endpoint.
- The SQL console is role-gated *and* screened *and* runs as `db_datareader`. The
  database permission is the real control; the screening is there to fail fast.
- The guided builder never concatenates caller text into SQL.
- Row caps and command timeouts bound the cost of any single query.
- `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy` and HSTS are set.

If you find a security problem, please open an issue without a working exploit.

## Licence

MIT — see [LICENSE](LICENSE).
