<#
.SYNOPSIS
    Collects device names from AD DNS into dbo.DeviceNameObservation.

.DESCRIPTION
    AD DNS knows names, but by IP. The warehouse needs them by MAC, because a MAC
    is the only identifier that survives a DHCP change. DimClient already holds the
    MAC/IP pairing, so this joins A records to it.

    AD DNS on this network is NOT clean, and the filtering below is the point of the
    script rather than incidental to it:

      * 127 of 136 A records are dynamic, registered by the clients themselves.
      * Many clients register their own MAC as the hostname - 00-A5-54-1F-8B-E1 is
        a true fact and a useless name.
      * Leases get reused without the old record being scavenged, so one address
        accumulates names: 10.20.1.2 is claimed by 06-CA-8B-D7-ED-EA,
        Cynthia-s-S20-FE, HOME-JASON and iPhone at once.

    So an address claimed by more than one real name is skipped entirely rather
    than resolved by guessing. Being unnamed is recoverable; being confidently
    wrong is what produced the ALIEN01 mess in the first place.

    Read-only against DNS. The only writes are to dbo.DeviceNameObservation, and
    they replace this source's rows wholesale so a deleted record stops being
    asserted.

.PARAMETER DnsServer
    A domain controller running the DNS role.

.PARAMETER Zone
    Forward lookup zone to read.

.PARAMETER SqlServer
    SQL Server holding the warehouse.

.PARAMETER StaleDays
    Ignore dynamic records not refreshed in this many days. A record older than
    the DHCP lease plus scavenging interval is describing a device that has gone.

.PARAMETER WhatIf
    Show what would be written without writing it.

.EXAMPLE
    .\Collect-AdDnsNames.ps1
    .\Collect-AdDnsNames.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$DnsServer = 'WINAD02',
    [string]$Zone      = 'bylotas.net',
    [string]$SqlServer = 'WINSERVER01',
    [string]$Database  = 'pihole',
    [int]$StaleDays    = 30
)

$ErrorActionPreference = 'Stop'

# A scheduled task's console output goes nowhere, so a failure at 00:30 is
# invisible. Transcript to a dated file beside the script, keeping a fortnight.
$script:LogDir = Join-Path $PSScriptRoot 'logs'
try {
    if (-not (Test-Path $script:LogDir)) { New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null }
    Start-Transcript -Path (Join-Path $script:LogDir ("{0}-{1}.log" -f
        [IO.Path]::GetFileNameWithoutExtension($PSCommandPath), (Get-Date -Format 'yyyyMMdd-HHmmss'))) | Out-Null
    Get-ChildItem $script:LogDir -Filter '*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-14) } |
        Remove-Item -Force -ErrorAction SilentlyContinue
} catch {
    # Logging is a convenience. Never let it stop the collection.
}

# Report a real exit code: Task Scheduler shows LastTaskResult, and a script that
# throws but exits 0 looks like a success in the history.
trap {
    Write-Host "  FAIL $($_.Exception.Message)" -ForegroundColor Red
    try { Stop-Transcript | Out-Null } catch { }
    exit 1
}

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  OK   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  WARN $m" -ForegroundColor Yellow }

# A hostname that is just a MAC in either separator style. True, and useless.
$MacShaped = '^[0-9a-fA-F]{2}([:-][0-9a-fA-F]{2}){5}$'

Step "Reading $Zone from $DnsServer"

$readZone = {
    param($z)
    Get-DnsServerResourceRecord -ZoneName $z -RRType A | ForEach-Object {
        [pscustomobject]@{
            HostName  = $_.HostName
            IP        = $_.RecordData.IPv4Address.IPAddressToString
            Timestamp = $_.Timestamp          # null for a static record
        }
    }
}

# Run in-process when this IS the DNS server. The scheduled task runs on the DC as
# SYSTEM, and a WinRM loopback from SYSTEM would need rights it does not have and
# does not need - the DNS role is right here.
$isLocal = $DnsServer -in @('.', 'localhost', $env:COMPUTERNAME, "$env:COMPUTERNAME.$env:USERDNSDOMAIN")
$records = if ($isLocal) {
    Ok 'reading the zone locally'
    & $readZone $Zone
} else {
    Invoke-Command -ComputerName $DnsServer -ArgumentList $Zone -ScriptBlock $readZone
}
Ok "$($records.Count) A records"

# ── Filter, loudly ──────────────────────────────────────────────────────────
Step 'Filtering'

$cutoff = (Get-Date).AddDays(-$StaleDays)
$usable = $records | Where-Object {
    $_.HostName -notin '@', '*' -and
    $_.HostName -notmatch $MacShaped -and
    $_.HostName -notmatch '^(DomainDnsZones|ForestDnsZones|gc)$' -and
    ($null -eq $_.Timestamp -or $_.Timestamp -ge $cutoff)
}
Ok "$($usable.Count) usable after dropping MAC-shaped, infrastructure and stale records"

# An address claimed by several real names cannot be resolved by choosing one, so
# do not choose. This is the lease-reuse case, and picking wrong here is exactly
# the failure this whole exercise is meant to end.
$contested = $usable | Group-Object IP | Where-Object { ($_.Group.HostName | Sort-Object -Unique).Count -gt 1 }
if ($contested) {
    Warn "$($contested.Count) address(es) claimed by more than one name - skipping them:"
    $contested | Select-Object -First 6 | ForEach-Object {
        Write-Host "         $($_.Name): $((($_.Group.HostName | Sort-Object -Unique) -join ', '))"
    }
}
$contestedIps = @($contested.Name)
$clean = $usable | Where-Object { $_.IP -notin $contestedIps }
Ok "$($clean.Count) records on unambiguous addresses"

# ── Join to MACs ────────────────────────────────────────────────────────────
Step "Joining to DimClient on $SqlServer"

$conn = New-Object System.Data.SqlClient.SqlConnection(
    "Server=$SqlServer;Database=$Database;Integrated Security=true;TrustServerCertificate=true")
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = 'SELECT ip, mac FROM dbo.DimClient WHERE mac IS NOT NULL'
$cmd.CommandTimeout = 120
$macByIp = @{}
$r = $cmd.ExecuteReader()
while ($r.Read()) { $macByIp[$r.GetString(0)] = $r.GetString(1).ToLowerInvariant() }
$r.Close()
Ok "$($macByIp.Count) addresses with a known MAC"

$rows = @{}
foreach ($rec in $clean) {
    $mac = $macByIp[$rec.IP]
    if (-not $mac) { continue }
    # One MAC can hold several addresses. Keep the first real name seen; they are
    # the same device, so any of its names will do and a static record sorts first.
    if (-not $rows.ContainsKey($mac)) {
        $rows[$mac] = [pscustomobject]@{ Mac = $mac; Name = $rec.HostName; Ip = $rec.IP }
    }
}
Ok "$($rows.Count) MAC(s) resolved to a name"

if ($rows.Count -eq 0) {
    Warn 'nothing to write'
    $conn.Close()
    return
}

# ── Write ───────────────────────────────────────────────────────────────────
Step 'Writing observations'

if ($WhatIfPreference) {
    $rows.Values | Sort-Object Name | Select-Object -First 25 |
        ForEach-Object { "  {0,-20} {1,-18} {2}" -f $_.Mac, $_.Ip, $_.Name }
    Warn "-WhatIf: $($rows.Count) row(s) not written"
    $conn.Close()
    return
}

if ($PSCmdlet.ShouldProcess("$SqlServer/$Database", "replace $($rows.Count) addns observations")) {
    $tx = $conn.BeginTransaction()
    try {
        # Replace this source wholesale, so a name that has gone from DNS stops
        # being asserted here rather than lingering as the winning answer forever.
        $del = $conn.CreateCommand()
        $del.Transaction = $tx
        $del.CommandText = "DELETE FROM dbo.DeviceNameObservation WHERE source = 'addns'"
        $removed = $del.ExecuteNonQuery()

        $ins = $conn.CreateCommand()
        $ins.Transaction = $tx
        $ins.CommandText = @'
INSERT INTO dbo.DeviceNameObservation (mac, source, name, ip, observed_utc)
VALUES (@mac, 'addns', @name, @ip, SYSUTCDATETIME());
'@
        [void]$ins.Parameters.Add('@mac',  [Data.SqlDbType]::VarChar, 17)
        [void]$ins.Parameters.Add('@name', [Data.SqlDbType]::NVarChar, 255)
        [void]$ins.Parameters.Add('@ip',   [Data.SqlDbType]::VarChar, 45)

        foreach ($row in $rows.Values) {
            $ins.Parameters['@mac'].Value  = $row.Mac
            $ins.Parameters['@name'].Value = $row.Name
            $ins.Parameters['@ip'].Value   = $row.Ip
            [void]$ins.ExecuteNonQuery()
        }

        $tx.Commit()
        Ok "replaced $removed row(s) with $($rows.Count)"
    } catch {
        $tx.Rollback()
        throw
    }
}

# ── What changed ────────────────────────────────────────────────────────────
Step 'Effect'

$check = $conn.CreateCommand()
$check.CommandTimeout = 300
$check.CommandText = @'
SELECT devices     = COUNT(*),
       from_addns  = SUM(CASE WHEN name_source = 'addns' THEN 1 ELSE 0 END),
       still_ftl   = SUM(CASE WHEN name_source = 'ftl'   THEN 1 ELSE 0 END),
       unnamed     = SUM(CASE WHEN name_source = 'ip'    THEN 1 ELSE 0 END)
FROM dbo.vClient WHERE mac IS NOT NULL;
'@
$r = $check.ExecuteReader()
while ($r.Read()) {
    "  {0} device(s) with a MAC: {1} named from AD DNS, {2} still on FTL, {3} unnamed" -f `
        $r['devices'], $r['from_addns'], $r['still_ftl'], $r['unnamed']
}
$r.Close()
$conn.Close()

Write-Host @"

  AD DNS can only name what it has a clean record for. The Omada controller is
  the better source - it is the DHCP server and it holds the names you assigned -
  so run Collect-OmadaNames.ps1 as well once its API credentials are configured.
"@ -ForegroundColor Cyan

try { Stop-Transcript | Out-Null } catch { }
exit 0
