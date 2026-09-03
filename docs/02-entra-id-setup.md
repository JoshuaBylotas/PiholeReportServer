# Entra ID setup

This is the complete path from an empty tenant to a working sign-in, including the app
roles that separate "can read reports" from "can run arbitrary SQL".

Everything below can be done in the Entra portal or with the Azure CLI. Both are given;
pick one.

---

## 1. Register the application

### Portal

1. Go to <https://entra.microsoft.com> → **Applications** → **App registrations** →
   **New registration**.
2. **Name:** `Pi-hole Report Server`
3. **Supported account types:** *Accounts in this organizational directory only
   (single tenant)*. This app is internal; there is no reason to accept other tenants.
4. **Redirect URI:** platform **Web**, value:
   ```
   https://<APP_HOST>/signin-oidc
   ```
5. **Register.**

Then copy two values from the **Overview** blade — you need both later:

| Overview field | Goes into |
|----------------|-----------|
| **Application (client) ID** | `AzureAd:ClientId` |
| **Directory (tenant) ID** | `AzureAd:TenantId` |

### Azure CLI

```bash
az ad app create \
  --display-name "Pi-hole Report Server" \
  --sign-in-audience AzureADMyOrg \
  --web-redirect-uris "https://<APP_HOST>/signin-oidc" \
  --enable-id-token-issuance true
```

```bash
# The two values you need
az ad app list --display-name "Pi-hole Report Server" \
  --query "[0].{clientId:appId}" -o tsv
az account show --query tenantId -o tsv
```

---

## 2. Redirect URIs and sign-out

Under **Authentication**:

- **Redirect URIs** (Web platform) — add one per host you will actually browse to:
  ```
  https://<APP_HOST>/signin-oidc
  https://localhost:7443/signin-oidc          ← only if you run locally
  ```
- **Front-channel logout URL:**
  ```
  https://<APP_HOST>/signout-oidc
  ```
- **Implicit grant and hybrid flows:** tick **ID tokens**. The app uses the
  authorization-code flow with PKCE, and `Microsoft.Identity.Web` requests an ID token
  through the hybrid flow; without this box the sign-in fails with `AADSTS700054`.
- Leave **Access tokens** unticked. This app is not an API.

> **These must match exactly**, scheme and trailing path included. A redirect URI
> mismatch is the single most common setup failure — see
> [troubleshooting](08-troubleshooting.md#aadsts50011-redirect-uri-mismatch).

### If you run behind a reverse proxy

IIS or any proxy terminating TLS will make Kestrel believe the request arrived over
HTTP, so the app generates `http://…/signin-oidc` and Entra rejects it.
`Program.cs` already calls `UseForwardedHeaders` for `X-Forwarded-Proto`; make sure the
proxy actually sends that header. See [deployment](06-deployment.md).

---

## 3. Client credential

The app needs a credential to redeem the authorization code. A **certificate** is
preferable; a secret is simpler.

### Option A — client secret

**Certificates & secrets** → **Client secrets** → **New client secret**.
Description `report-server`, expiry 12 or 24 months.

**Copy the value immediately** — it is never shown again.

```bash
az ad app credential reset --id <CLIENT_ID> --years 1 --query password -o tsv
```

Store it as a secret, never in `appsettings.json`:

```powershell
# Development
dotnet user-secrets --project src/PiholeReportServer set "AzureAd:ClientSecret" "<CLIENT_SECRET>"

# Production — environment variable, note the double underscore
setx AzureAd__ClientSecret "<CLIENT_SECRET>" /M
```

> Put the expiry date in your calendar. When a secret lapses, sign-in breaks with
> `AADSTS7000215` and the cause is not obvious from the error.

### Option B — certificate (preferred for production)

```powershell
$cert = New-SelfSignedCertificate `
  -Subject "CN=PiholeReportServer" `
  -CertStoreLocation "Cert:\LocalMachine\My" `
  -KeyExportPolicy NonExportable `
  -KeySpec Signature `
  -NotAfter (Get-Date).AddYears(2)

Export-Certificate -Cert $cert -FilePath .\PiholeReportServer.cer
$cert.Thumbprint
```

Upload the `.cer` under **Certificates & secrets** → **Certificates**, then configure:

```jsonc
"AzureAd": {
  "ClientCertificates": [
    {
      "SourceType": "StoreWithThumbprint",
      "CertificateStorePath": "LocalMachine/My",
      "CertificateThumbprint": "<THUMBPRINT>"
    }
  ]
}
```

Grant the app pool identity read access to the private key, or the app fails at startup
with a key-not-found error.

---

## 4. API permissions

The baseline registration already has `User.Read` (delegated, Microsoft Graph), which
is all that sign-in needs. Click **Grant admin consent** so users are not each prompted.

**Only if you intend to use Entra authentication to SQL** (`Sql:AuthMode` of
`EntraOnBehalfOf` or `EntraApp`) add the Azure SQL delegated permission:

1. **API permissions** → **Add a permission** → **APIs my organization uses**
2. Search for **Azure SQL Database** (app ID `022907d3-0f1b-48f7-badc-1ba6abab6d66`)
3. **Delegated permissions** → `user_impersonation`
4. **Grant admin consent**

```bash
az ad app permission add --id <CLIENT_ID> \
  --api 022907d3-0f1b-48f7-badc-1ba6abab6d66 \
  --api-permissions c39ef2d1-04ce-46dc-8b5f-e9a5c60f0fc9=Scope
az ad app permission admin-consent --id <CLIENT_ID>
```

Read [SQL Server setup](03-sql-server-setup.md#can-my-sql-server-actually-accept-entra-tokens)
**before** doing this — a stand-alone SQL Server, including SQL Express, cannot accept
Entra tokens at all, and this permission will not change that.

---

## 5. App roles

Two roles separate reading from arbitrary querying.

**App roles** → **Create app role**, twice:

| Display name | Value | Allowed member types | Description |
|--------------|-------|----------------------|-------------|
| Report Viewer | `Report.Viewer` | Users/Groups | Browse and run reports and the guided builder |
| SQL Author | `Report.SqlAuthor` | Users/Groups | Additionally run free-text SQL in the query console |

Or declare both at once by editing the **Manifest**:

```jsonc
"appRoles": [
  {
    "allowedMemberTypes": [ "User" ],
    "displayName": "Report Viewer",
    "description": "Browse and run reports and the guided builder.",
    "value": "Report.Viewer",
    "id": "3f7a1d84-6c2e-4b91-9f0a-2d5e8c1b4a70",
    "isEnabled": true
  },
  {
    "allowedMemberTypes": [ "User" ],
    "displayName": "SQL Author",
    "description": "Run free-text SQL in the query console.",
    "value": "Report.SqlAuthor",
    "id": "9b2c5e10-8a34-4d6f-b7e1-0c3f9a6d2b58",
    "isEnabled": true
  }
]
```

> The two `id` GUIDs must be unique within the application but are otherwise arbitrary.
> Generate your own with `[guid]::NewGuid()` if you prefer.

`Report.SqlAuthor` is deliberately **not** a superset of `Report.Viewer` in the
manifest — the `Viewer` policy in `Program.cs` accepts *either* role, so a SQL author
does not need both assigned.

---

## 6. Assign people to roles

Roles do nothing until they are assigned.

**Enterprise applications** → *Pi-hole Report Server* → **Users and groups** →
**Add user/group** → pick the user or group, pick the role.

```bash
# Find the service principal (not the app registration)
SP=$(az ad sp list --display-name "Pi-hole Report Server" --query "[0].id" -o tsv)
APP=$(az ad app list --display-name "Pi-hole Report Server" --query "[0].appId" -o tsv)
ROLE=$(az ad app show --id "$APP" \
        --query "appRoles[?value=='Report.SqlAuthor'].id | [0]" -o tsv)
USER=$(az ad user show --id "<user@domain>" --query id -o tsv)

az rest --method POST \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$SP/appRoleAssignedTo" \
  --body "{\"principalId\":\"$USER\",\"resourceId\":\"$SP\",\"appRoleId\":\"$ROLE\"}"
```

### Require assignment (recommended)

By default **anyone in the tenant** can sign in even without a role. To restrict access
to assigned users only:

**Enterprise applications** → *Pi-hole Report Server* → **Properties** →
**Assignment required?** → **Yes**.

---

## 7. Turn on role enforcement in the app

The application ships with role checking **off**, so a fresh install is usable before
roles exist. Once roles are assigned, switch it on:

```jsonc
"AzureAd": {
  "RequireAppRoles": true
}
```

| `RequireAppRoles` | Effect |
|-------------------|--------|
| `false` (default) | Any authenticated tenant user is treated as a viewer. The SQL console still requires `Report.SqlAuthor`. |
| `true` | A viewer must hold `Report.Viewer` **or** `Report.SqlAuthor`. Everyone else gets 403. |

> Leaving this `false` in production means every account in your tenant can read your
> DNS history. Set it to `true` once assignments are in place.

---

## 8. Configure and verify

```powershell
dotnet user-secrets --project src/PiholeReportServer set "AzureAd:TenantId"     "<TENANT_ID>"
dotnet user-secrets --project src/PiholeReportServer set "AzureAd:ClientId"     "<CLIENT_ID>"
dotnet user-secrets --project src/PiholeReportServer set "AzureAd:ClientSecret" "<CLIENT_SECRET>"
dotnet user-secrets --project src/PiholeReportServer set "AzureAd:Domain"       "<TENANT_DOMAIN>"

dotnet run --project src/PiholeReportServer
```

Check, in order:

1. **`/healthz` returns 200 without signing in.** Liveness must not require auth.
2. **`/` redirects to `login.microsoftonline.com`.** If instead you get a 500 saying
   *"Unable to obtain configuration from …/REPLACE_WITH_TENANT_GUID/…"*, the tenant ID
   never got applied — the placeholder from `appsettings.json` is still in play.
3. **After signing in, `/Diagnostics`** lists your claims. Confirm `roles` appears and
   contains what you assigned. If `roles` is missing entirely, the assignment was made
   against the wrong object (app registration rather than enterprise application), or
   you need to sign out and back in to get a fresh token.
4. **A user without `Report.SqlAuthor` sees no SQL tab**, and a direct request to
   `/Query/Sql` returns 403 rather than the page.

---

## What Entra ID does and does not protect

Worth being explicit, because it is easy to over-read "secured with Entra ID":

**It does** control who can open the site, who can reach the SQL console, and it gives
you conditional access, MFA and sign-in logs for the application.

**It does not** change who the *database* sees. Unless you are running one of the Entra
SQL modes — which needs Azure SQL, Managed Instance, or Arc-enabled SQL Server 2022+ —
every query reaches SQL Server as the one service login the app is configured with.
Per-user database auditing and row-level security are therefore *not* in play by
default. [SQL Server setup](03-sql-server-setup.md) covers exactly what is and is not
possible on each platform.
