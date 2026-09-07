# Troubleshooting

Symptoms, causes, and fixes. Several of these are failures that actually occurred while
building this system rather than hypotheticals.

---

## Sign-in

### `AADSTS50011` redirect URI mismatch

> The redirect URI specified in the request does not match the redirect URIs configured
> for the application.

The URI the app generated is not registered. The error page shows the URI it *sent* —
compare it character by character with the app registration.

Usual causes:

1. **Scheme is `http` instead of `https`.** Behind IIS or any TLS-terminating proxy,
   Kestrel sees plain HTTP and builds an `http://` reply URL. `Program.cs` calls
   `UseForwardedHeaders`, so ensure the proxy actually sends
   `X-Forwarded-Proto: https`.
2. **Host name differs** — `reports` vs `reports.example.internal`, or an IP instead of
   a name. Register every host you will actually browse to.
3. **Port included or omitted** unexpectedly. `https://host:443/...` and
   `https://host/...` are different strings to Entra.
4. **Path wrong.** It must be `/signin-oidc`.

### 500 with `IDX20803: Unable to obtain configuration from … REPLACE_WITH_TENANT_GUID`

Configuration never reached the app — the placeholder from `appsettings.json` is still
in effect. Check spelling and casing:

```powershell
dotnet user-secrets --project src/PiholeReportServer list
# Production: note the DOUBLE underscore
[Environment]::GetEnvironmentVariable("AzureAd__TenantId", "Machine")
```

`AzureAd:TenantId` in JSON and user-secrets; `AzureAd__TenantId` as an environment
variable. A single underscore silently does nothing.

### `No authenticationScheme was specified, and there was no DefaultChallengeScheme found`

A wiring bug, not a configuration problem. The default challenge scheme must be the
scheme `AddMicrosoftIdentityWebApp` actually registers:

```csharp
// Correct
builder.Services
    .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));
```

Passing the *configuration section name* (`"AzureAd"`) as the scheme leaves the
challenge pointing at a scheme that was never registered, and every anonymous request
500s instead of redirecting to sign-in.

### `AADSTS650057` invalid resource, sign-in fails at `/signin-oidc`

> Message contains error: 'invalid_client', error_description: 'AADSTS650057: Invalid
> resource. The client has requested access to a resource which is not listed in the
> requested permissions in the client's application registration.'

Entra issues the authorization code, then refuses to redeem it, so the browser lands on
`/signin-oidc` with an HTTP 400 and the app logs an `AuthenticationFailureException`.

The sign-in request asked for a resource the app registration has no permission for.
The usual culprit is the Azure SQL scope: `EnableTokenAcquisitionToCallDownstreamApi`
must only be given `Sql:TokenScope` up front when `Sql:AuthMode` is `EntraApp` or
`EntraOnBehalfOf`. The `SqlLogin` and `Integrated` modes never need an Azure SQL
permission, and requesting it anyway breaks sign-in completely.

Check what is actually being asked for — browse to the site, copy the `Location` header
of the 302, and read its `scope` parameter. For `SqlLogin` it should be exactly
`openid profile offline_access`.

If you genuinely want an Entra SQL mode, grant and admin-consent the Azure SQL
`user_impersonation` permission — see
[Entra ID setup §4](02-entra-id-setup.md#4-api-permissions) — and confirm your platform
can accept Entra tokens at all
([SQL Server setup](03-sql-server-setup.md#can-my-sql-server-actually-accept-entra-tokens)).

### `message.State is null or empty` at `/signin-oidc`

Harmless on its own: somebody browsed straight to `/signin-oidc`. That endpoint is the
OIDC callback, not a page — Entra reaches it with an HTTP **POST** carrying `code` and
`state` (`response_mode=form_post`), so a plain GET has nothing to validate and returns
400. Start at `/` instead.

Worth checking the log rather than assuming, though: a real sign-in failure and a stray
manual GET both surface as a 400 on the same path, and the real one will have a
different inner exception (see `AADSTS650057` above).

### A redeploy appears to succeed but the old build keeps running

`robocopy` returns an exit code **8 or higher** when files failed to copy, and with
in-process hosting the running worker process holds `PiholeReportServer.dll` open, so
the copy is refused while everything *looks* fine.

Always check the exit code — 0-7 is success, 8+ is failure — and stop the app pool (or
drop an `app_offline.htm` in the site root) before copying:

```powershell
Stop-WebAppPool -Name 'PiholeReportServer'
robocopy <src> <dest> /MIR /XD logs
icacls <dest> /grant 'BUILTIN\IIS_IUSRS:(OI)(CI)(RX)'   # /MIR resets these
Start-WebAppPool -Name 'PiholeReportServer'
```

Confirm the deployed binary is actually the one you built:

```powershell
(Get-Item '<dest>\PiholeReportServer.dll').LastWriteTime
```

### `AADSTS7000215` invalid client secret

The secret expired, or the **secret ID** was copied instead of the **value**. Only the
value is usable, and it is shown exactly once. Generate a new one:

```bash
az ad app credential reset --id <CLIENT_ID> --years 1 --query password -o tsv
```

### `AADSTS700054` response_type 'id_token' is not enabled

Tick **ID tokens** under **Authentication** → *Implicit grant and hybrid flows*.

### Signed in, but 403 on every page

`RequireAppRoles` is `true` and you hold neither role. Check `/Diagnostics` — if the
`roles` claim is absent:

- The assignment was made on the **app registration** instead of the **enterprise
  application**. Roles are assigned under *Enterprise applications → Users and groups*.
- Or the token predates the assignment. Sign out fully and back in.

### Everyone is signed out after every app pool recycle

Data protection keys are not persisted, so the cookie-encryption key changes on
restart. See [deployment](06-deployment.md#two-iis-specific-gotchas) — either enable
`loadUserProfile` or call `PersistKeysToFileSystem`.

---

## SQL Server

### `Login failed for user` with an Entra auth mode

The instance cannot accept Entra tokens. A stand-alone SQL Server — very much including
SQL Express — has no such capability; it requires Azure SQL, Managed Instance, or an
Azure Arc-enabled SQL Server 2022+.

```sql
SELECT SERVERPROPERTY('Edition'), SERVERPROPERTY('ProductVersion');
SELECT name FROM sys.server_principals WHERE type_desc LIKE '%EXTERNAL%';
```

Express edition plus no external principals means switch to `Sql:AuthMode` of
`SqlLogin`. Full matrix in
[SQL Server setup](03-sql-server-setup.md#can-my-sql-server-actually-accept-entra-tokens).

### `A connection was successfully established … but then an error occurred during the login process`

Almost always TLS. A default install presents a self-signed certificate that the client
does not trust.

```jsonc
"Sql": { "Encrypt": true, "TrustServerCertificate": true }
```

Do **not** "fix" this with `Encrypt: false` — that sends credentials in cleartext.
`TrustServerCertificate: true` still encrypts; it only skips validating the certificate.

### `The server was not found or was not accessible`

```powershell
Test-NetConnection -ComputerName <SQL_HOST> -Port 1433
```

- TCP/IP is disabled by default on SQL Express — enable it in SQL Server Configuration
  Manager and restart the instance.
- Named instances need the SQL Browser service, or an explicit port in `Sql:Port`.
- Firewall on the SQL host.

### `VIEW DATABASE STATE permission was denied` on /Diagnostics

The object inventory reads `sys.dm_db_partition_stats`:

```sql
GRANT VIEW DATABASE STATE TO pihole_report_ro;
```

### A dimension table shows "Missing" on /Diagnostics

`dims.py` has never run, or failed. On the Pi-hole host:

```bash
sudo systemctl start pihole-sqlsync-dims.service
journalctl -u pihole-sqlsync-dims -n 40 --no-pager
```

---

## The data pipeline

### `MAX(ts)` is frozen days in the past, but the service looks fine

The classic bad-row deadlock. `systemctl status` reports **activating**, not *failed*,
so a casual glance suggests health.

```bash
systemctl show pihole-sqlsync -p NRestarts --value      # thousands = wedged
journalctl -u pihole-sqlsync -n 20 --no-pager
```

Look for the same error repeating with an unchanging watermark:

```
[start] table=dbo.PiholeQueries watermark(id)=85163833
[error] OperationalError: Could not decode to UTF-8 column 'domain' with text '...'
```

One row with non-UTF-8 bytes in `domain` kills the process before the batch commits, so
the watermark never advances and `Restart=always` re-reads the same batch forever.

Fix — both parts — in
[the sync pipeline](05-pihole-sqlsync.md#the-bad-row-failure-mode--read-this-before-you-trust-restartalways):
a lenient `text_factory`, and a row-by-row fallback so the watermark always advances.

Afterwards:

```bash
sudo systemctl reset-failed pihole-sqlsync   # so the next stall is visible
```

### Reports show far fewer clients than exist on the network

Those clients are not using Pi-hole for DNS. See
[Pi-hole setup §4](04-pihole-setup.md#4-make-sure-pi-hole-actually-sees-your-clients).
Devices with hard-coded resolvers, or anything using DNS-over-HTTPS, bypass Pi-hole
entirely and cannot appear.

### The Pi-hole host has no IPv4 address

The static address is already claimed by something else. NetworkManager refuses to
create a duplicate and leaves the interface IPv6-only.

```bash
sudo arping -D -I eth0 -c 3 <PIHOLE_IP>
ip neigh | grep <PIHOLE_IP>
```

A leftover secondary IP on another host is a real cause. Clear the stale PTR record
afterwards too.

### `pihole-FTL sqlite3` returns nothing at all, with no error

Double quotes. SQLite treats `"..."` as an **identifier**, not a string literal:

```bash
# Wrong - returns nothing
... "SELECT COUNT(*)||\" \"||MAX(id) FROM query_storage;"

# Right
... "SELECT COUNT(*), MAX(id) FROM query_storage;"
```

Costly because it fails *silently* — in a script the empty output shifts every
subsequent parsed field, so unrelated checks start reporting nonsense.

---

## Reports and queries

### A `LIKE N'%' + NCHAR(65533) + N'%'` search matches every single row

`domain` is `varchar` under `SQL_Latin1_General_CP1_CI_AS`. Comparing an `nvarchar`
pattern against it makes `U+FFFD` **collation-ignorable**, so the pattern degenerates
to "match anything".

```sql
-- Wrong: matches the entire table
WHERE domain LIKE N'%' + NCHAR(65533) + N'%'

-- Right
WHERE CHARINDEX('?', domain) > 0
-- or force byte-exact comparison
WHERE domain COLLATE Latin1_General_BIN2 LIKE '%?%'
```

### `COUNT(*)` and `COUNT(DISTINCT id)` disagree

If they came from **two separate queries**, that is expected — the loader inserted rows
between them. Take both in one statement:

```sql
SELECT COUNT_BIG(*), COUNT_BIG(DISTINCT id) FROM dbo.PiholeQueries;
```

They can never genuinely differ; the primary key on `id` guarantees it.

### `Must declare the scalar variable "@x"`

The SQL references a parameter that `reports.json` does not declare. Every `@name` in
the file needs a matching `parameters` entry. Declared-but-unused is fine; the reverse
is not.

### A report times out

```jsonc
"Sql": { "CommandTimeoutSeconds": 120 }
```

Better: narrow the range, or add an index. Confirm the plan is doing what you expect:

```sql
SET STATISTICS IO, TIME ON;
-- the report SQL with real parameter values
```

A scan of `PiholeQueries` where you expected a seek usually means the `ts` predicate is
not `SARGable` — `WHERE CAST(ts AS date) = '2026-09-01'` cannot use the index, while
`WHERE ts >= '2026-09-01' AND ts < '2026-09-02'` can.

### "Truncated at 5,000" on every result

That is the row cap doing its job. Raise it if you need to, but the CSV export has its
own much higher limit and is the better answer for bulk extraction:

```jsonc
"Reporting": { "MaxRows": 5000, "MaxExportRows": 250000 }
```

### The SQL console rejects a legitimate query

`SqlGuard` uses a keyword denylist, so a column or alias named after a reserved verb
can trip it. Quote it:

```sql
SELECT [update] FROM dbo.SomeView     -- bracketed: accepted
```

Bracketed identifiers are stripped before screening precisely for this case. If a
genuine false positive remains, use the builder or add a pre-canned report — do not
loosen the guard.

---

## Build and run

### `NU1902` / `NU1903` package vulnerability, build fails

`Directory.Build.props` sets `TreatWarningsAsErrors`, so NuGet audit findings break the
build. That is intentional. Update the package rather than suppressing it:

```bash
dotnet list package --vulnerable --include-transitive
dotnet add src/PiholeReportServer package Microsoft.Identity.Web
```

### `NETSDK1022: Duplicate 'Content' items`

The Web SDK already includes `.json` as content. Use `Update`, not `Include`:

```xml
<Content Update="Reports\reports.json" CopyToOutputDirectory="PreserveNewest" />
```

### `ASPDEPR005: ForwardedHeadersOptions.KnownNetworks is obsolete`

Renamed in .NET 10 — use `KnownIPNetworks`.

### Tests cannot see an `internal` member

The web project exposes internals to the test assembly via an `AssemblyAttribute` in
`PiholeReportServer.csproj`. If you rename the test project, update that attribute.

---

## Diagnostics quick reference

```bash
# --- Pi-hole host ---
systemctl status pihole-sqlsync
systemctl show pihole-sqlsync -p NRestarts --value
journalctl -u pihole-sqlsync --since '1 hour ago' | grep -E '\[warn\]|\[skip\]|\[error\]'
sudo -u pihole pihole-FTL sqlite3 -readonly /etc/pihole/pihole-FTL.db \
  "SELECT COUNT(*), MAX(id) FROM query_storage;"
timedatectl status
```

```sql
-- --- SQL Server ---
SELECT COUNT_BIG(*) AS rows, MAX(id) AS max_id, MAX(ts) AS newest,
       DATEDIFF(minute, MAX(ts), SYSUTCDATETIME()) AS lag_minutes
FROM dbo.PiholeQueries;

SELECT SUSER_SNAME() AS login, USER_NAME() AS db_user;
```

```powershell
# --- Web host ---
curl.exe -k https://<APP_HOST>/healthz
Get-Service PiholeReportServer
```

And in the browser: **/Diagnostics** is the single fastest check — it shows your claims,
the effective SQL identity, and a present/missing inventory of every expected object.

## AI inference

### The host answers locally but the report server is told the model does not exist

`/api/tags` returns `{"models":[]}` and every generate request 404s, while on the
inference host itself `ollama pull`, `ollama ps` and a `curl` to `127.0.0.1` all
work perfectly.

**Two Ollama servers are running.** Windows permits two processes to listen on one
port when one binds the IPv6 wildcard and the other binds IPv4 loopback:

```
::           11434     <- ollama serve with OLLAMA_HOST=0.0.0.0 (the scheduled task)
127.0.0.1    11434     <- a second server on stock defaults (usually the desktop app)
```

Neither fails to bind, so neither logs a problem. Every local client reaches the
loopback server; a remote client arriving over IPv4 reaches the wildcard one. The
model downloads into one server's store and the report server talks to the other.

Confirm it, then repair it:

```powershell
Get-NetTCPConnection -LocalPort 11434 -State Listen |
    Select-Object LocalAddress, LocalPort, OwningProcess
tools\setup\repair-ollama-host.ps1
```

The repair script consolidates the model store rather than downloading the model a
second time. If the Ollama **desktop app** is installed, remove it from the Startup
folder as well, or it starts a second server again at the next logon.

**The tell is the keep-alive.** `ollama ps` showing `UNTIL` about 5 minutes means the
server answering is on stock defaults, because the scheduled task sets
`OLLAMA_KEEP_ALIVE=60m`. A server started by this project always reports ~59 minutes.

### A cold benchmark reports an absurd prompt-eval rate

The first request after a server starts includes the model load in
`prompt_eval_duration`. Loading a 9.6 GB model showed up once as "39 prompt tokens
at 2.1 tok/s", which reads as a catastrophic fault and was ordinary disk I/O — the
same measurement warm was 203 tok/s. Always issue one throwaway request before
timing anything.

### Answers are correct but far slower than expected

Check the **Analyst** page for "Running on the standby inference host". The preferred
host was unreachable, so requests moved to `Ai:FallbackEndpoint`, which is a slower
machine running a smaller model.

This is by design: the preferred host here is a laptop that leaves the network, and
without a standby the AI features and the nightly job produce nothing while it is
away. Nothing needs restarting — the preferred host is retried every
`Ai:FallbackRetryPrimarySeconds` (default 120) and traffic returns on its own.

**/Diagnostics** probes both hosts and names each one, so a standby that is itself
broken is visible before it is needed.

Failover is deliberately narrow. It triggers only on a transport failure — refused
connection, DNS failure, no route. It does **not** trigger on a timeout or an HTTP
error status, because both of those mean a server did answer, and the standby is the
slower machine: failing over on slowness would only produce a slower failure.

### A category total looks far too low

You filtered `dbo.DomainCategory.category` directly. Four corpora fill that
column and they disagree on names, so `category = 'advertising'` finds 2,310 of
10,497 rows and reports it as though that were all of them.

Use `dbo.vDomainCategory` and group on `canonical_category`. See
[Read categories through dbo.vDomainCategory](01-architecture.md) for the map.

```sql
-- Wrong: misses "ads", which is what ut1 and blp call the same thing.
WHERE category = 'advertising'

-- Right.
WHERE canonical_category = 'advertising'
```

If a value looks like it is missing entirely, check for an unmapped one:

```sql
SELECT DISTINCT source_category FROM dbo.vDomainCategory WHERE is_unmapped = 1;
```

Add it to `tools/schema/CategoryMap.sql` and re-run that script.

### The Analyst forgets the previous turn

A follow-up like "can you order this descending" behaves as though it were the
first question.

The conversation id is bound from the request field named by
`AnalystModel.ConversationField`. If the form or the fetch sends a different
name, **nothing errors** — the property simply stays null, `GetOrStart` begins a
fresh conversation, and every turn loses its history. That is exactly what
happened once, with the form posting `ConversationId` while the binder wanted
`c`.

The view now takes the name from that constant and the script reads the input's
own `name`, so the three cannot disagree. If follow-ups stop working again,
check that the POST actually carries the field:

```
F12 → Network → the ?handler=Ask request → Payload
```

It must include the conversation id under the same name the page model binds. A
conversation also expires after two hours idle, and only the last six turns are
replayed — beyond that, older context is genuinely gone rather than broken.

### The classifier on PI5-01 cannot reach the inference host

```
curl: (7) Failed to connect to 10.20.0.139 port 11434
```

The inference host's firewall rule is scoped to specific addresses. PI5-01 fetches
page content **and** calls the model to classify it, so it is an inference client
too and needs to be on that list:

```powershell
Set-NetFirewallRule -DisplayName 'Ollama (report server only)' `
  -RemoteAddress '10.20.0.15','10.20.0.16','10.20.0.173','10.20.0.174'
```

Note that Windows Firewall evaluates **Block rules before Allow rules**. Adding a
companion "block everything else" rule on the same port closes the port for the
permitted addresses too. Do not add one — the default inbound action is already
Block, so anything not matching the Allow is refused without help.

### `env: 'python3\r': No such file or directory` on the Pi

The fetch and classify scripts were copied from Windows and kept CRLF line endings,
which breaks the shebang. `python3 script.py` still works, which is why this can go
unnoticed until something invokes them directly.

```bash
sudo sed -i 's/\r$//' /opt/pihole-fetch/*.py
```

