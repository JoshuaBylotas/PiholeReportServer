<#
.SYNOPSIS
    Collects device names from the Omada controller into dbo.DeviceNameObservation.

.DESCRIPTION
    The controller is the best source of device names on this network, for two
    reasons that no other source has together:

      * It IS the DHCP server, so it sees the hostname each device asks for at
        lease time - before any DNS registration can go stale.
      * It is where devices get named by hand, and a name someone typed is worth
        more than one a device chose for itself.

    It is also keyed on MAC natively. Every other source is keyed on IP and has to
    be joined through DimClient, which is exactly where reverse DNS goes wrong.

    Uses the OpenAPI (Platform Integration), not the legacy session login: it takes
    a client id and secret rather than an operator password, is scoped read-only,
    and does not break when someone signs in to the web UI.

    POLLING, NOT A WEBHOOK. Device names change rarely, so a schedule is ample. A
    webhook would need an unauthenticated inbound endpoint on a site that is
    otherwise entirely behind Entra, and a missed delivery would be lost - where a
    poll simply catches up next time.

.PARAMETER Controller
    Base URL of the controller, including the port.

.PARAMETER CredentialPath
    JSON file holding omadacId, clientId and clientSecret. Kept outside the repo:
    private/ is gitignored precisely for this.

    To create the credentials, in the controller:
      Settings -> Platform Integration -> OpenAPI -> Add New App
        Mode:  Client Mode
        Scope: the site you want, with VIEW permission - it never needs to write
    Then copy the Client ID and Client Secret into the file:

      { "omadacId": "d42ca0ef...", "clientId": "...", "clientSecret": "..." }

.PARAMETER WhatIf
    Show what would be written without writing it.

.EXAMPLE
    .\Collect-OmadaNames.ps1 -WhatIf
    .\Collect-OmadaNames.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Controller     = 'https://10.20.0.3:8043',
    [string]$CredentialPath = (Join-Path $PSScriptRoot '..\..\private\omada.json'),
    [string]$SqlServer      = 'WINSERVER01',
    [string]$Database       = 'pihole',
    [switch]$IncludeOffline
)

$ErrorActionPreference = 'Stop'

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  OK   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  WARN $m" -ForegroundColor Yellow }

# The controller uses a self-signed certificate by default. This is a LAN call to
# a host named in the parameter, not a trust decision about the internet.
$PSDefaultParameterValues['Invoke-RestMethod:SkipCertificateCheck'] = $true

# ── Credentials ─────────────────────────────────────────────────────────────
Step 'Credentials'

if (-not (Test-Path $CredentialPath)) {
    throw @"
No credential file at $CredentialPath

Create OpenAPI credentials in the controller:
  Settings -> Platform Integration -> OpenAPI -> Add New App
    Mode:  Client Mode
    Scope: your site, VIEW permission only

Then write them to that path:
  { "omadacId": "...", "clientId": "...", "clientSecret": "..." }

private/ is gitignored, so the secret stays out of the repository.
"@
}

$cred = Get-Content $CredentialPath -Raw | ConvertFrom-Json
foreach ($f in 'omadacId', 'clientId', 'clientSecret') {
    if (-not $cred.$f) { throw "$CredentialPath is missing '$f'." }
}
Ok "loaded for omadacId $($cred.omadacId.Substring(0, 8))…"

# ── Token ───────────────────────────────────────────────────────────────────
Step 'Authenticating'

$tokenUri = "$Controller/openapi/authorize/token?grant_type=client_credentials"
$tokenBody = @{
    omadacId      = $cred.omadacId
    client_id     = $cred.clientId
    client_secret = $cred.clientSecret
} | ConvertTo-Json

$tok = Invoke-RestMethod -Uri $tokenUri -Method Post -Body $tokenBody `
         -ContentType 'application/json' -TimeoutSec 30

if ($tok.errorCode -ne 0) {
    throw "Token request failed: errorCode=$($tok.errorCode) $($tok.msg)"
}
$token = $tok.result.accessToken
if (-not $token) { throw 'The controller returned no access token.' }
Ok "token acquired, expires in $($tok.result.expiresIn)s"

$headers = @{ Authorization = "AccessToken=$token" }

# ── Sites ───────────────────────────────────────────────────────────────────
Step 'Sites'

$sites = Invoke-RestMethod -Headers $headers -TimeoutSec 30 `
           -Uri "$Controller/openapi/v1/$($cred.omadacId)/sites?pageSize=100&page=1"
if ($sites.errorCode -ne 0) { throw "Site list failed: $($sites.errorCode) $($sites.msg)" }

$siteList = @($sites.result.data)
if ($siteList.Count -eq 0) {
    throw 'The credential can see no sites. Check the scope on the OpenAPI app.'
}
$siteList | ForEach-Object { "  $($_.name)  ($($_.siteId))" }

# ── Clients ─────────────────────────────────────────────────────────────────
Step 'Clients'

$clients = [System.Collections.Generic.List[object]]::new()

foreach ($site in $siteList) {
    $page = 1
    while ($true) {
        $uri = "$Controller/openapi/v1/$($cred.omadacId)/sites/$($site.siteId)/clients" +
               "?page=$page&pageSize=100"
        $resp = Invoke-RestMethod -Headers $headers -Uri $uri -TimeoutSec 60
        if ($resp.errorCode -ne 0) { throw "Client list failed: $($resp.errorCode) $($resp.msg)" }

        $batch = @($resp.result.data)
        if ($batch.Count -eq 0) { break }
        $batch | ForEach-Object { $clients.Add($_) }

        # totalRows is authoritative; stop when this page completed the set.
        if (($page * 100) -ge $resp.result.totalRows) { break }
        $page++
    }
    Ok "$($site.name): $($clients.Count) client(s) so far"
}

if ($clients.Count -eq 0) { Warn 'the controller reported no clients'; return }

# ── Shape it ────────────────────────────────────────────────────────────────
Step 'Selecting names'

# A hostname that is simply the MAC is true and useless. So is a name equal to the
# IP. Both would otherwise outrank a real name from AD DNS, which is the opposite
# of the point.
$macShaped = '^[0-9a-fA-F]{2}([:-][0-9a-fA-F]{2}){5}$'

$rows = @{}
$noName = 0
foreach ($c in $clients) {
    if (-not $c.mac) { continue }
    $mac = ($c.mac -replace '-', ':').ToLowerInvariant()
    if ($mac -notmatch '^[0-9a-f]{2}(:[0-9a-f]{2}){5}$') { continue }

    # "name" is what someone typed in the controller; "hostName" is what the device
    # asked for at DHCP. The typed one wins - that is the whole reason to prefer
    # this source.
    $name = $null
    foreach ($candidate in @($c.name, $c.hostName)) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        $t = $candidate.Trim()
        if ($t -match $macShaped) { continue }
        if ($t -eq $c.ip) { continue }
        $name = $t
        break
    }
    if (-not $name) { $noName++; continue }

    if (-not $IncludeOffline -and $c.PSObject.Properties['active'] -and -not $c.active) {
        continue
    }

    $rows[$mac] = [pscustomobject]@{
        Mac  = $mac
        Name = $name
        Ip   = $c.ip
        Type = $c.deviceType
    }
}
Ok "$($rows.Count) named device(s); $noName had only a MAC-shaped or empty name"

if ($WhatIfPreference) {
    $rows.Values | Sort-Object Name | Select-Object -First 40 |
        ForEach-Object { "  {0,-20} {1,-16} {2}" -f $_.Mac, $_.Ip, $_.Name }
    Warn "-WhatIf: $($rows.Count) row(s) not written"
    return
}

# ── Write ───────────────────────────────────────────────────────────────────
Step 'Writing observations'

$conn = New-Object System.Data.SqlClient.SqlConnection(
    "Server=$SqlServer;Database=$Database;Integrated Security=true;TrustServerCertificate=true")
$conn.Open()

if ($PSCmdlet.ShouldProcess("$SqlServer/$Database", "replace $($rows.Count) omada observations")) {
    $tx = $conn.BeginTransaction()
    try {
        # Replace wholesale: a device removed from the controller should stop being
        # asserted by it, not keep winning forever on a stale row.
        $del = $conn.CreateCommand()
        $del.Transaction = $tx
        $del.CommandText = "DELETE FROM dbo.DeviceNameObservation WHERE source = 'omada'"
        $removed = $del.ExecuteNonQuery()

        $ins = $conn.CreateCommand()
        $ins.Transaction = $tx
        $ins.CommandText = @'
INSERT INTO dbo.DeviceNameObservation (mac, source, name, ip, device_type, observed_utc)
VALUES (@mac, 'omada', @name, @ip, @type, SYSUTCDATETIME());
'@
        [void]$ins.Parameters.Add('@mac',  [Data.SqlDbType]::VarChar, 17)
        [void]$ins.Parameters.Add('@name', [Data.SqlDbType]::NVarChar, 255)
        [void]$ins.Parameters.Add('@ip',   [Data.SqlDbType]::VarChar, 45)
        [void]$ins.Parameters.Add('@type', [Data.SqlDbType]::NVarChar, 60)

        foreach ($row in $rows.Values) {
            $ins.Parameters['@mac'].Value  = $row.Mac
            $ins.Parameters['@name'].Value = $row.Name
            $ins.Parameters['@ip'].Value   = if ($row.Ip)   { $row.Ip }   else { [DBNull]::Value }
            $ins.Parameters['@type'].Value = if ($row.Type) { $row.Type } else { [DBNull]::Value }
            [void]$ins.ExecuteNonQuery()
        }
        $tx.Commit()
        Ok "replaced $removed row(s) with $($rows.Count)"
    } catch {
        $tx.Rollback()
        throw
    }
}

# ── Effect ──────────────────────────────────────────────────────────────────
Step 'Effect'

$check = $conn.CreateCommand()
$check.CommandTimeout = 300
$check.CommandText = @'
SELECT devices   = COUNT(*),
       omada     = SUM(CASE WHEN name_source = 'omada' THEN 1 ELSE 0 END),
       addns     = SUM(CASE WHEN name_source = 'addns' THEN 1 ELSE 0 END),
       still_ftl = SUM(CASE WHEN name_source = 'ftl'   THEN 1 ELSE 0 END),
       unnamed   = SUM(CASE WHEN name_source = 'ip'    THEN 1 ELSE 0 END)
FROM dbo.vClient WHERE mac IS NOT NULL;
'@
$r = $check.ExecuteReader()
while ($r.Read()) {
    "  {0} device(s): {1} from Omada, {2} from AD DNS, {3} still FTL, {4} unnamed" -f `
        $r['devices'], $r['omada'], $r['addns'], $r['still_ftl'], $r['unnamed']
}
$r.Close()
$conn.Close()
