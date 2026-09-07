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

# Omada sends the token as "AccessToken=<value>", which is not the "scheme value"
# shape PowerShell validates the Authorization header against - it refuses to send
# it at all with "The format of value ... is invalid". -SkipHeaderValidation on
# every call below turns that check off; the header itself is exactly what the
# controller documents.
$PSDefaultParameterValues['Invoke-RestMethod:SkipHeaderValidation'] = $true

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

    if (-not $IncludeOffline -and $c.PSObject.Properties['active'] -and -not $c.active) {
        continue
    }

    # Two different things live here, and conflating them was a real bug: the first
    # run resolved two devices to "wlan0" because a device-announced hostname was
    # ranked above AD DNS, beating "firestick-0a0a273294170242".
    #
    #   name      typed into the controller by a person - authoritative
    #   hostName  announced by the device at DHCP - sometimes excellent, sometimes
    #             "wlan0" or "localhost"
    #
    # The controller falls back to the hostname when nothing has been typed, so a
    # name equal to hostName is not evidence anyone chose it.
    $typed    = if ([string]::IsNullOrWhiteSpace($c.name)) { $null } else { $c.name.Trim() }
    $announced = if ([string]::IsNullOrWhiteSpace($c.hostName)) { $null } else { $c.hostName.Trim() }

    if ($typed -and $announced -and $typed -eq $announced) { $typed = $null }

    $name = $null; $src = $null
    if ($typed   -and $typed   -notmatch $macShaped -and $typed   -ne $c.ip) { $name = $typed;   $src = 'omada' }
    elseif ($announced -and $announced -notmatch $macShaped -and $announced -ne $c.ip) { $name = $announced; $src = 'omadadhcp' }

    if (-not $name) { $noName++; continue }

    # Reverse DNS suffixes the domain; the controller sometimes carries it too.
    # Strip it so one device does not appear under two spellings.
    $name = $name -replace '\.bylotas\.(net|com)$', ''

    $rows[$mac] = [pscustomobject]@{
        Mac    = $mac
        Name   = $name
        Ip     = $c.ip
        Type   = $c.deviceType
        Source = $src
    }
}
$typedCount = @($rows.Values | Where-Object { $_.Source -eq 'omada' }).Count
Ok "$($rows.Count) named device(s): $typedCount named in the controller, $($rows.Count - $typedCount) self-announced; $noName unusable"

if ($WhatIfPreference) {
    $rows.Values | Sort-Object Source, Name | Select-Object -First 40 |
        ForEach-Object { "  {0,-11} {1,-20} {2,-16} {3}" -f $_.Source, $_.Mac, $_.Ip, $_.Name }
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
        $del.CommandText = "DELETE FROM dbo.DeviceNameObservation WHERE source IN ('omada', 'omadadhcp')"
        $removed = $del.ExecuteNonQuery()

        $ins = $conn.CreateCommand()
        $ins.Transaction = $tx
        $ins.CommandText = @'
INSERT INTO dbo.DeviceNameObservation (mac, source, name, ip, device_type, observed_utc)
VALUES (@mac, @source, @name, @ip, @type, SYSUTCDATETIME());
'@
        [void]$ins.Parameters.Add('@source', [Data.SqlDbType]::VarChar, 10)
        [void]$ins.Parameters.Add('@mac',  [Data.SqlDbType]::VarChar, 17)
        [void]$ins.Parameters.Add('@name', [Data.SqlDbType]::NVarChar, 255)
        [void]$ins.Parameters.Add('@ip',   [Data.SqlDbType]::VarChar, 45)
        [void]$ins.Parameters.Add('@type', [Data.SqlDbType]::NVarChar, 60)

        foreach ($row in $rows.Values) {
            $ins.Parameters['@source'].Value = $row.Source
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
       omada     = SUM(CASE WHEN name_source = 'omada'     THEN 1 ELSE 0 END),
       addns     = SUM(CASE WHEN name_source = 'addns'     THEN 1 ELSE 0 END),
       announced = SUM(CASE WHEN name_source = 'omadadhcp' THEN 1 ELSE 0 END),
       still_ftl = SUM(CASE WHEN name_source = 'ftl'   THEN 1 ELSE 0 END),
       unnamed   = SUM(CASE WHEN name_source = 'ip'    THEN 1 ELSE 0 END)
FROM dbo.vClient WHERE mac IS NOT NULL;
'@
$r = $check.ExecuteReader()
while ($r.Read()) {
    "  {0} device(s): {1} named in Omada, {2} from AD DNS, {3} self-announced, {4} still FTL, {5} unnamed" -f `
        $r['devices'], $r['omada'], $r['addns'], $r['announced'], $r['still_ftl'], $r['unnamed']
}
$r.Close()
$conn.Close()
