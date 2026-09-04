# SQL Server setup

Covers the database and schema, the read-only login the web app uses, and — the part
that catches people out — which authentication modes your particular SQL Server can
actually support.

---

## Can my SQL Server actually accept Entra tokens?

Read this first, because it determines everything else.

"Sign in with Entra ID, then use that identity against SQL Server" is a reasonable
thing to want, and on some platforms it works exactly as you would hope. On others it
is not merely unconfigured but **impossible**, and no amount of app configuration will
change that.

| Platform | Entra ID authentication to SQL? |
|----------|--------------------------------|
| Azure SQL Database | **Yes** |
| Azure SQL Managed Instance | **Yes** |
| SQL Server 2022+ **Azure Arc-enabled**, with Entra auth enabled | **Yes** |
| SQL Server 2022 stand-alone (not Arc-connected) | **No** |
| SQL Server 2019 and earlier | **No** |
| SQL Server **Express**, any version | **No** in practice — Arc-enabled Entra auth is not available for Express |

Check what you have:

```sql
SELECT @@VERSION;
SELECT SERVERPROPERTY('ProductVersion')  AS version,
       SERVERPROPERTY('Edition')          AS edition,
       SERVERPROPERTY('IsIntegratedSecurityOnly') AS windows_auth_only;

-- Any Entra principals already present? Empty means Entra auth is not in use.
SELECT name, type_desc
FROM sys.server_principals
WHERE type_desc LIKE '%EXTERNAL%';
```

If `Edition` says *Express Edition* and the external-principals query returns nothing,
you are on the "No" row. Use `SqlLogin` (below) and accept that the database sees one
service identity. Entra ID still governs **who can reach the application**, which is
where the access control that matters for a reporting tool actually lives.

---

## The four authentication modes

Set `Sql:AuthMode` to one of these.

### `SqlLogin` — default, works everywhere

A dedicated SQL login with `db_datareader` and nothing else.

```jsonc
"Sql": {
  "Server": "<SQL_HOST>",
  "Database": "<SQL_DB>",
  "AuthMode": "SqlLogin",
  "TrustServerCertificate": true   // only for a self-signed server cert
}
```

Credentials never go in `appsettings.json`:

```powershell
dotnet user-secrets --project src/PiholeReportServer set "Sql:UserId"   "pihole_report_ro"
dotnet user-secrets --project src/PiholeReportServer set "Sql:Password" "<PASSWORD>"
# Production:
setx Sql__UserId   "pihole_report_ro" /M
setx Sql__Password "<PASSWORD>" /M
```

### `Integrated` — Windows / Active Directory

The app connects as its own service account (app pool identity or the Windows service
account). Requires a domain-joined host and a matching database user.

```jsonc
"Sql": { "AuthMode": "Integrated" }
```

This is **not** per-user impersonation. Flowing the browser user's Windows identity
through to SQL needs Kerberos constrained delegation, and it cannot work at all when
sign-in happens through Entra ID rather than integrated Windows auth — the app never
holds a Kerberos ticket for the user.

### `EntraApp` — the application's own Entra identity

A managed identity or the app registration authenticating to Azure SQL. The database
sees one principal, but it is an Entra principal, so Azure RBAC and Entra auditing
apply.

```jsonc
"Sql": {
  "AuthMode": "EntraApp",
  "TokenScope": "https://database.windows.net//.default"
}
```

### `EntraOnBehalfOf` — the signed-in user's identity

The one that does what "use these credentials to authenticate to SQL" describes. The
app exchanges the user's session for a SQL-scoped token, so **SQL Server sees the
actual person**: per-user permissions, row-level security and auditing all apply.

```jsonc
"Sql": {
  "AuthMode": "EntraOnBehalfOf",
  "TokenScope": "https://database.windows.net//.default"
}
```

Requirements, all of them:

1. A platform from the "Yes" rows above.
2. The Azure SQL `user_impersonation` delegated permission granted and admin-consented
   — see [Entra ID setup §4](02-entra-id-setup.md#4-api-permissions).
3. **Every user** who will run a report has a database principal:
   ```sql
   CREATE USER [alice@<TENANT_DOMAIN>] FROM EXTERNAL PROVIDER;
   ALTER ROLE db_datareader ADD MEMBER [alice@<TENANT_DOMAIN>];
   ```
   Or, far less tedious, grant to a group:
   ```sql
   CREATE USER [Pihole Report Readers] FROM EXTERNAL PROVIDER;
   ALTER ROLE db_datareader ADD MEMBER [Pihole Report Readers];
   ```

If the platform cannot accept the token, `SqlConnectionFactory` catches login errors
18456/18452 and raises a message naming this document rather than leaving you with a
bare "login failed".

> **Note on the double slash** in `https://database.windows.net//.default` — it is not
> a typo. The Azure SQL resource identifier ends in `/`, and `.default` is appended to
> it. Removing one slash produces an invalid-scope error.

---

## Create the database and schema

If `pihole-sqlsync` has already been running, the fact table exists and you can skip to
[the read-only login](#create-the-read-only-login).

```sql
IF DB_ID('pihole') IS NULL
    CREATE DATABASE pihole;
GO
USE pihole;
GO

CREATE TABLE dbo.PiholeQueries
(
    id          bigint        NOT NULL,
    ts          datetime2(0)  NOT NULL,   -- UTC
    type        int           NULL,
    status      int           NULL,
    status_text varchar(32)   NULL,
    domain      varchar(255)  NULL,
    client      varchar(255)  NULL,
    forward     varchar(255)  NULL,
    reply_type  int           NULL,
    reply_time  float         NULL,       -- seconds
    dnssec      int           NULL,
    ede         int           NULL,
    CONSTRAINT PK_PiholeQueries PRIMARY KEY CLUSTERED (id)
);
GO
```

The primary key on `id` is what makes the loader idempotent: a re-run cannot duplicate
rows, and the watermark is simply `MAX(id)`.

### Indexes

Without these, every report is a full scan of tens of millions of rows.

```sql
-- Almost every report filters on a time range.
CREATE NONCLUSTERED INDEX IX_PiholeQueries_ts
    ON dbo.PiholeQueries (ts)
    INCLUDE (domain, client, status, type);

-- "Top domains", and the GravityDomains join.
CREATE NONCLUSTERED INDEX IX_PiholeQueries_domain_ts
    ON dbo.PiholeQueries (domain, ts);

-- Per-client reports.
CREATE NONCLUSTERED INDEX IX_PiholeQueries_client_ts
    ON dbo.PiholeQueries (client, ts)
    INCLUDE (domain);
GO
```

Build these **after** a large initial backfill, not before — maintaining three indexes
while inserting 20M+ rows roughly doubles the load time.

```sql
-- Space used, once populated
SELECT i.name,
       SUM(ps.used_page_count) * 8 / 1024 AS mb
FROM sys.dm_db_partition_stats ps
     JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE ps.object_id = OBJECT_ID('dbo.PiholeQueries')
GROUP BY i.name
ORDER BY mb DESC;
```

### Dimension tables

Created and refreshed by `dims.py` — see [the sync pipeline](05-pihole-sqlsync.md).
Their shape:

```sql
CREATE TABLE dbo.DimStatus (
    status      int PRIMARY KEY,
    status_text varchar(32) NOT NULL
);

CREATE TABLE dbo.DimType (
    type      int PRIMARY KEY,
    type_text varchar(32) NOT NULL
);

CREATE TABLE dbo.DimClient (
    ip          varchar(64)  PRIMARY KEY,
    name        varchar(255) NULL,   -- device name, NOT "hostname"
    mac         varchar(32)  NULL,
    mac_vendor  varchar(128) NULL,   -- OUI lookup, NOT "vendor"
    interface   varchar(32)  NULL,
    num_queries bigint       NULL,
    last_query  datetime2    NULL
);

CREATE TABLE dbo.Adlists (
    id           int PRIMARY KEY,
    address      varchar(500) NULL,
    enabled      bit          NULL,
    comment      varchar(255) NULL,
    number       int          NULL,
    status       int          NULL,
    type         int          NULL,
    date_updated datetime2    NULL
);

CREATE TABLE dbo.GravityDomains (
    domain    varchar(255) NOT NULL,
    adlist_id int          NOT NULL
);
CREATE CLUSTERED INDEX IX_GravityDomains_domain ON dbo.GravityDomains (domain);
```

`GravityDomains` holds millions of rows and is joined on `domain` by the
would-be-blocked reports, so that clustered index is not optional.

---

## Create the read-only login

The web application must never connect as `sa` or as the loader's own account. The
loader writes; the report server only reads.

```sql
USE master;
GO
CREATE LOGIN pihole_report_ro
    WITH PASSWORD = '<STRONG_PASSWORD>',
         CHECK_POLICY = ON;
GO

USE pihole;
GO
CREATE USER pihole_report_ro FOR LOGIN pihole_report_ro;
ALTER ROLE db_datareader ADD MEMBER pihole_report_ro;
GO
```

`/Diagnostics` reads `sys.dm_db_partition_stats` for its object inventory, which needs
one extra grant:

```sql
GRANT VIEW DATABASE STATE TO pihole_report_ro;
GO
```

Then confirm the login genuinely cannot write. This is the control the SQL console
relies on, so it is worth proving rather than assuming:

```sql
EXECUTE AS USER = 'pihole_report_ro';
    SELECT TOP 1 id, ts, domain FROM dbo.PiholeQueries;   -- succeeds
    BEGIN TRY
        DELETE TOP (1) FROM dbo.PiholeQueries;             -- must fail
        PRINT 'PROBLEM: the read-only login can delete rows';
    END TRY
    BEGIN CATCH
        PRINT 'Good — write refused: ' + ERROR_MESSAGE();
    END CATCH
REVERT;
GO
```

### Optional: cap what a runaway query can consume

Belt and braces alongside the app's own row cap and command timeout:

```sql
CREATE WORKLOAD GROUP wg_reporting
    WITH (REQUEST_MAX_CPU_TIME_SEC = 60, MAX_DOP = 2)
    USING "default";
```

Resource Governor is Enterprise-only. On Express, the app's `CommandTimeoutSeconds` and
`MaxRows` are your limits.

---

## Networking and TLS

- Enable **TCP/IP** in SQL Server Configuration Manager; a default install of Express
  has it off.
- Fixed port 1433, or set `Sql:Port` for a named instance. If you rely on a named
  instance by name, the SQL Browser service must be running.
- Firewall: allow 1433 inbound from the web host, and from the Pi-hole host for the
  loader.

```powershell
New-NetFirewallRule -DisplayName "SQL Server (report server)" `
  -Direction Inbound -Protocol TCP -LocalPort 1433 `
  -RemoteAddress <APP_HOST_IP>,<PIHOLE_IP> -Action Allow
```

`Sql:Encrypt` defaults to `true`, which is correct. A default SQL Server install
presents a self-signed certificate, so you must either:

- set `Sql:TrustServerCertificate: true` — acceptable on a trusted LAN, and it still
  encrypts the connection; it only skips validating the certificate; or
- install a certificate the client trusts, and leave `TrustServerCertificate` false.

Do not set `Encrypt: false` to make a connection work. That sends credentials in
cleartext.

---

## Verify end to end

```powershell
sqlcmd -S <SQL_HOST> -U pihole_report_ro -P "<PASSWORD>" -d <SQL_DB> -C -Q `
  "SELECT COUNT_BIG(*) AS rows, MIN(ts) AS oldest, MAX(ts) AS newest FROM dbo.PiholeQueries;"
```

Then start the app and open **/Diagnostics**. It shows the effective login, the
database user, the server version, and a present/missing row count for each expected
object — the fastest way to confirm the pieces line up.
