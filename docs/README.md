# Documentation

Everything needed to stand up the Pi-hole Report Server and the data pipeline behind it.

> **This repository is public.** Every document here uses placeholders such as
> `<SQL_HOST>`, `<TENANT_ID>` and `<PIHOLE_IP>` rather than real host names, addresses
> or identifiers. The actual values for a given installation belong in the
> `private/` folder at the repository root, which is excluded by `.gitignore`.
> See [`private/README.md`](../private/README.md).

## Read in this order

| # | Document | What it covers |
|---|----------|----------------|
| 1 | [Architecture](01-architecture.md) | How the pieces fit together, and the data model |
| 2 | [Entra ID setup](02-entra-id-setup.md) | App registration, redirect URIs, secrets, app roles, assigning users |
| 3 | [SQL Server setup](03-sql-server-setup.md) | Database, schema, the read-only login, and the four authentication modes |
| 4 | [Pi-hole setup](04-pihole-setup.md) | Preparing the Pi-hole host so its query log can be read safely |
| 5 | [The sync pipeline](05-pihole-sqlsync.md) | Installing and operating `pihole-sqlsync`, the loader that fills the warehouse |
| 6 | [Deployment](06-deployment.md) | Running the site under IIS, Kestrel + Windows Service, or Docker |
| 7 | [Reports](07-reports.md) | The pre-canned report library, and how to add your own |
| 8 | [Troubleshooting](08-troubleshooting.md) | Symptoms, causes and fixes, including every failure we actually hit |

## Quick start

Assuming the warehouse already exists and you only need the web application:

```powershell
git clone https://github.com/JoshuaBylotas/PiholeReportServer.git
cd PiholeReportServer

# 1. Entra ID values (see 02-entra-id-setup.md)
dotnet user-secrets --project src/PiholeReportServer set "AzureAd:TenantId" "<TENANT_ID>"
dotnet user-secrets --project src/PiholeReportServer set "AzureAd:ClientId" "<CLIENT_ID>"
dotnet user-secrets --project src/PiholeReportServer set "AzureAd:ClientSecret" "<CLIENT_SECRET>"
dotnet user-secrets --project src/PiholeReportServer set "AzureAd:Domain" "<TENANT_DOMAIN>"

# 2. SQL values (see 03-sql-server-setup.md)
dotnet user-secrets --project src/PiholeReportServer set "Sql:Server" "<SQL_HOST>"
dotnet user-secrets --project src/PiholeReportServer set "Sql:UserId" "pihole_report_ro"
dotnet user-secrets --project src/PiholeReportServer set "Sql:Password" "<PASSWORD>"

# 3. Run
dotnet run --project src/PiholeReportServer
```

Then browse to <https://localhost:7443>. You should be redirected straight to a
Microsoft sign-in page — the whole site sits behind the Entra ID gate, with the sole
exception of `/healthz`.

## Validating report SQL

Neither `dotnet build` nor the unit tests can see the database schema, and report SQL
ships as content — so a wrong column name compiles, passes CI, and fails only when
somebody opens that report. After changing any report SQL, or after a loader schema
change, run:

```powershell
.\tools\Validate-ReportSql.ps1 -Server <SQL_HOST>
```

It binds every pre-canned report and every builder permutation with
`sys.sp_describe_first_result_set` — a full parse and bind with no execution, so it is
safe and fast against a table with tens of millions of rows. Run `dotnet test` first: a
test dumps the builder permutations for the script to pick up.

## Conventions used in these documents

| Placeholder | Meaning |
|-------------|---------|
| `<TENANT_ID>` | Entra ID directory (tenant) GUID |
| `<TENANT_DOMAIN>` | Primary tenant domain, e.g. `contoso.onmicrosoft.com` |
| `<CLIENT_ID>` | Application (client) ID of the app registration |
| `<CLIENT_SECRET>` | Client secret value for the app registration |
| `<APP_HOST>` | Public host name the site is served on, e.g. `reports.example.internal` |
| `<SQL_HOST>` | SQL Server host, or `host\instance` for a named instance |
| `<SQL_DB>` | Database holding the warehouse (`pihole` by default) |
| `<PIHOLE_IP>` | IP address of the Pi-hole host |
| `<PIHOLE_USER>` | SSH account on the Pi-hole host |
