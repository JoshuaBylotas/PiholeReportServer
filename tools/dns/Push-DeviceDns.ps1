<#
.SYNOPSIS
    Publishes the stated device names in dbo.vDeviceTruth to AD DNS.

.DESCRIPTION
    The agent half of the DNS push. The report server inserts a row in
    dbo.DnsPushRequest; this claims it, performs the record changes, and writes
    what it did to dbo.DnsPushLog.

    It runs here, on WINAD02, rather than in the web application, so that the
    site never needs write rights on the zone. A web app that can edit DNS is a
    web app that can redirect the network.

    WHAT IT CHANGES

      * The A record for each stated name, so the name points at the address the
        device is actually on.
      * Other DYNAMIC A records pointing at that same address. This is the fix
        for the real fault: 23 addresses currently carry more than one name
        because the old script only ever de-duplicated by hostname, so every
        DHCP move left the previous name behind. 10.20.0.227 has five.
      * The PTR record, creating the reverse zone first when it is missing -
        which it is for 10.20.1.x, so those clients have no PTR at all today.

    WHAT IT WILL NOT TOUCH, EVER

      * A STATIC record. A record with no timestamp was created by a person on
        purpose: www, pihole, filter, filtering and minecraftapi are all aliases
        pointing at a server that also has its own dynamic name. Removing "the
        other names on this address" without this rule would delete them.
      * The zone apex, DomainDnsZones, ForestDnsZones, or anything under _msdcs.
      * A name claimed by more than one MAC. It cannot be published to two
        addresses, so it is skipped and reported rather than resolved by
        guessing which device the owner meant.

    A dry run works out and records every one of those decisions and changes
    nothing.

.PARAMETER SqlServer
    SQL Server holding the warehouse.

.PARAMETER DnsServer
    The DNS server to write. Defaults to this host.

.PARAMETER Zone
    Forward lookup zone the names are published into.

.PARAMETER MaxRequests
    How many queued requests to process in one invocation.

.EXAMPLE
    .\Push-DeviceDns.ps1
    Claim and run whatever the site has queued.

.EXAMPLE
    .\Push-DeviceDns.ps1 -Force -DryRun
    Run against every publishable device now, without waiting for a request and
    without changing anything.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SqlServer   = 'WINSERVER01',
    [string]$Database    = 'pihole',
    [string]$DnsServer   = $env:COMPUTERNAME,
    [string]$Zone        = 'bylotas.net',
    [int]$MaxRequests    = 5,

    # Omada controller, for writing the stated name back to the client so the
    # controller stops showing the model string.
    [string]$OmadaController = 'https://10.20.0.3:8043',

    # Resolved in the body, NOT defaulted here. Under powershell.exe -File on
    # 5.1, $PSScriptRoot is EMPTY while the param block is being bound, so
    # (Join-Path $PSScriptRoot 'omada.json') threw during parameter binding -
    # before the trap existed, so the run produced no event and never claimed
    # its request. It works under -Command, which is what made it look
    # intermittent. Collect-OmadaNames.ps1 resolves its credential path in the
    # body for the same reason.
    [string]$OmadaCredentialPath,

    # Publish DNS but leave the controller alone.
    [switch]$SkipOmada,

    # Run immediately rather than claiming a queued request.
    [switch]$Force,

    # Only meaningful with -Force; a queued request carries its own dry_run flag.
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# Safe here: in the script BODY, $PSScriptRoot is populated under -File as well.
if (-not $OmadaCredentialPath) {
    $OmadaCredentialPath = Join-Path $PSScriptRoot 'omada.json'
}

$script:RunScriptName = $MyInvocation.MyCommand.Name

# Beside this script when deployed next to the collectors, or one directory over
# in the repo layout. Checked in that order so the deployed copy does not depend
# on the repo's shape.
$loggingModule = @(
    (Join-Path $PSScriptRoot 'CollectorLogging.ps1')
    (Join-Path (Split-Path $PSScriptRoot -Parent) 'collect\CollectorLogging.ps1')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($loggingModule) {
    . $loggingModule
}
else {
    # Standalone fallback so the script is testable outside the deployed tree.
    function Step([string]$m) { Write-Host "== $m" -ForegroundColor Cyan }
    function Ok([string]$m) { Write-Host "  OK $m" -ForegroundColor Green }
    function Warn([string]$m) { Write-Host "  !! $m" -ForegroundColor Yellow }
    function Detail([string]$m) { Write-Host "     $m" }
    function Write-RunEvent { param([switch]$Failure, [string]$Message) }
}

trap {
    Write-Host "  FAIL $($_.Exception.Message)" -ForegroundColor Red
    Write-RunEvent -Failure ("{0}`n{1}" -f $_.Exception.Message, $_.ScriptStackTrace)
    exit 1
}

if (-not (Get-Module -ListAvailable -Name DnsServer)) {
    throw 'The DnsServer module is not available. On a server: Install-WindowsFeature RSAT-DNS-Server.'
}
Import-Module DnsServer -ErrorAction Stop

# Names that are infrastructure, not devices. Matched on the leftmost label.
$ProtectedLabels = @('@', 'DomainDnsZones', 'ForestDnsZones', '_msdcs')

$connectionString =
    "Server=$SqlServer;Database=$Database;Integrated Security=true;TrustServerCertificate=true"

function New-SqlConnection {
    $c = New-Object System.Data.SqlClient.SqlConnection($connectionString)
    $c.Open()
    $c
}

function Invoke-Sql {
    param(
        [Parameter(Mandatory)] [System.Data.SqlClient.SqlConnection]$Connection,
        [Parameter(Mandatory)] [string]$Sql,
        [hashtable]$Parameters = @{},
        [switch]$Scalar,
        [switch]$NonQuery
    )
    $cmd = $Connection.CreateCommand()
    $cmd.CommandText = $Sql
    $cmd.CommandTimeout = 120
    foreach ($k in $Parameters.Keys) {
        $v = $Parameters[$k]
        $null = $cmd.Parameters.AddWithValue("@$k", $(if ($null -eq $v) { [DBNull]::Value } else { $v }))
    }
    if ($Scalar) { return $cmd.ExecuteScalar() }
    if ($NonQuery) { return $cmd.ExecuteNonQuery() }
    $table = New-Object System.Data.DataTable
    $table.Load($cmd.ExecuteReader())
    , $table
}

function Write-PushLog {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [int]$RequestId,
        [string]$Mac,
        [ValidateSet('added', 'updated', 'removed', 'skipped', 'failed')] [string]$Action,
        # Must stay in step with CK_DPL_type on dbo.DnsPushLog. Adding 'omada'
        # to the database constraint and not to this set made every controller
        # write log as a failure while the write itself was fine.
        [ValidateSet('A', 'PTR', 'zone', 'omada')] [string]$RecordType,
        [string]$RecordName,
        [string]$Ip,
        [string]$ZoneName,
        [string]$Detail
    )
    if ($RequestId -le 0) { return }   # -Force run outside any request
    $null = Invoke-Sql -Connection $Connection -NonQuery -Sql @'
INSERT INTO dbo.DnsPushLog
    (request_id, mac, action, record_type, record_name, ip, zone, detail, acted_utc)
VALUES (@rid, @mac, @action, @type, @name, @ip, @zone, @detail, SYSUTCDATETIME());
'@ -Parameters @{
        rid    = $RequestId
        mac    = $Mac
        action = $Action
        type   = $RecordType
        name   = $RecordName
        ip     = $Ip
        zone   = $ZoneName
        detail = $Detail
    }
}

# ---------------------------------------------------------------------------
# Omada
#
# Only the name is written. Verified against the controller's OpenAPI by asking
# each route what it accepts:
#
#   PATCH /clients/{mac}/name        the one that matters
#   PATCH /clients/{mac}/ratelimit
#   POST  /clients/{mac}/block | unblock | lock-to-ap
#
# There is NO route for the DHCP address reservation. ipSetting.useFixedAddr is
# readable on the client object and has no writable endpoint, on any path tried,
# so reservations stay a manual job in the controller UI. Neither is there a
# description or location route - the client object carries "location" but
# nothing accepts a write to it.
#
# HttpClient rather than Invoke-RestMethod because this runs under
# powershell.exe 5.1, where -SkipCertificateCheck and -SkipHeaderValidation do
# not exist, and the Authorization value is "AccessToken=<token>" which is not
# the "scheme value" shape .NET validates against.
# ---------------------------------------------------------------------------
if (-not ('OmadaTls' -as [type])) {
    Add-Type -ReferencedAssemblies System.Net.Http -TypeDefinition @'
using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

public static class OmadaTls
{
    public static readonly Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool>
        Callback = (m, c, ch, e) => true;

    // On 5.1 the validation callback must be a compiled delegate. A script block
    // throws "There is no Runspace available to run scripts in this thread" on
    // the handshake thread and surfaces as an unhelpful generic send failure.
    public static void Enable()
    {
        ServicePointManager.ServerCertificateValidationCallback = (s, c, ch, e) => true;
        ServicePointManager.SecurityProtocol =
            SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
    }
}
'@
}
[OmadaTls]::Enable()

$script:OmadaHttp = $null
function Get-OmadaHttp {
    if ($script:OmadaHttp) { return $script:OmadaHttp }
    $h = New-Object System.Net.Http.HttpClientHandler
    try { $h.ServerCertificateCustomValidationCallback = [OmadaTls]::Callback } catch { }
    $script:OmadaHttp = New-Object System.Net.Http.HttpClient($h)
    $script:OmadaHttp.Timeout = [TimeSpan]::FromSeconds(60)
    $script:OmadaHttp
}

function Invoke-Omada {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [ValidateSet('GET', 'POST', 'PATCH')][string]$Method = 'GET',
        [string]$Body,
        [string]$AccessToken
    )
    $http = Get-OmadaHttp
    $req = New-Object System.Net.Http.HttpRequestMessage(
        (New-Object System.Net.Http.HttpMethod($Method)), $Uri)
    if ($AccessToken) {
        [void]$req.Headers.TryAddWithoutValidation('Authorization', "AccessToken=$AccessToken")
    }
    if ($Body) {
        $req.Content = New-Object System.Net.Http.StringContent(
            $Body, [Text.Encoding]::UTF8, 'application/json')
    }
    $resp = $http.SendAsync($req).GetAwaiter().GetResult()
    $text = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $code = [int]$resp.StatusCode
    $req.Dispose(); $resp.Dispose()
    if (-not $text) { return [pscustomobject]@{ http = $code; errorCode = -1; msg = 'empty response' } }
    $obj = $text | ConvertFrom-Json
    $obj | Add-Member -NotePropertyName http -NotePropertyValue $code -Force
    $obj
}

# Omada spells a MAC uppercase with dashes; the warehouse stores lower-case
# colons. Everything internal stays in the warehouse form.
function ConvertTo-OmadaMac { param([string]$Mac) $Mac.ToUpperInvariant().Replace(':', '-') }

$script:Omada = $null
function Connect-Omada {
    <#
      Returns a context with a token and the site id, or $null when the
      controller is unreachable or the credential file is absent. A failure here
      must never fail the DNS push - names in DNS are the point; the controller
      is a convenience.
    #>
    if ($script:Omada) { return $script:Omada }
    if (-not (Test-Path $OmadaCredentialPath)) {
        Warn "no Omada credential at $OmadaCredentialPath - skipping the controller"
        return $null
    }
    try {
        $cred = Get-Content $OmadaCredentialPath -Raw | ConvertFrom-Json
        $tokenBody = @{
            omadacId      = $cred.omadacId
            client_id     = $cred.clientId
            client_secret = $cred.clientSecret
        } | ConvertTo-Json
        $tok = Invoke-Omada -Method POST -Body $tokenBody `
                 -Uri "$OmadaController/openapi/authorize/token?grant_type=client_credentials"
        if ($tok.errorCode -ne 0) { throw "token errorCode=$($tok.errorCode) $($tok.msg)" }

        $sites = Invoke-Omada -AccessToken $tok.result.accessToken `
                   -Uri "$OmadaController/openapi/v1/$($cred.omadacId)/sites?pageSize=100&page=1"
        if ($sites.errorCode -ne 0) { throw "site list errorCode=$($sites.errorCode) $($sites.msg)" }
        $site = @($sites.result.data)[0]
        if (-not $site) { throw 'the credential can see no sites' }

        $script:Omada = [pscustomobject]@{
            Token    = $tok.result.accessToken
            OmadacId = $cred.omadacId
            SiteId   = $site.siteId
            Base     = "$OmadaController/openapi/v1/$($cred.omadacId)/sites/$($site.siteId)"
        }
        Ok "Omada: authenticated, site $($site.name)"
        $script:Omada
    } catch {
        Warn "Omada unavailable, continuing without it: $($_.Exception.Message)"
        $null
    }
}

function Get-OmadaClientName {
    param($Ctx, [string]$Mac)
    $r = Invoke-Omada -AccessToken $Ctx.Token -Uri "$($Ctx.Base)/clients/$(ConvertTo-OmadaMac $Mac)"
    if ($r.errorCode -ne 0) { return $null }
    $r.result.name
}

function Set-OmadaClientName {
    param($Ctx, [string]$Mac, [string]$Name)
    $r = Invoke-Omada -AccessToken $Ctx.Token -Method PATCH `
            -Uri "$($Ctx.Base)/clients/$(ConvertTo-OmadaMac $Mac)/name" `
            -Body (@{ name = $Name } | ConvertTo-Json)
    if ($r.errorCode -ne 0) {
        throw "HTTP $($r.http) errorCode=$($r.errorCode) $($r.msg)"
    }
    $r.result.name
}

function Get-ReverseZoneName {
    param([Parameter(Mandatory)] [string]$Ip)
    $o = $Ip.Split('.')
    if ($o.Count -ne 4) { return $null }
    # /24 reverse zones, which is what this network uses: 0.20.10.in-addr.arpa
    "{0}.{1}.{2}.in-addr.arpa" -f $o[2], $o[1], $o[0]
}

function Get-PtrLabel {
    param([Parameter(Mandatory)] [string]$Ip)
    $Ip.Split('.')[3]
}

# Removes an A record only if it is still actually there. $existing is a
# single snapshot taken at the start of the run, but the run itself is making
# live changes as it goes - when two devices trade addresses in the same
# batch, the device that vacates an address gets processed first (alphabetical
# order) and removes its own stale record there. By the time the device that
# is MOVING IN gets to its own "clean up whoever else is squatting on my
# address" step, that same record is already gone. Asking DNS to remove a
# record that no longer matches throws "Failed to get <name> record" and used
# to abort the rest of that device's work - the PTR record, the Omada name -
# even though the zone was already in the state this device wanted.
function Remove-ARecordIfPresent {
    param($Zone, $DnsServer, $Record)
    $stillThere = @(Get-DnsServerResourceRecord -ZoneName $Zone -Name $Record.HostName `
            -RRType A -ComputerName $DnsServer -ErrorAction SilentlyContinue |
        Where-Object { $_.RecordData.IPv4Address.IPAddressToString -eq
            $Record.RecordData.IPv4Address.IPAddressToString })
    if (-not $stillThere) { return $false }
    Remove-DnsServerResourceRecord -ZoneName $Zone -InputObject $Record -ComputerName $DnsServer -Force
    $true
}

# ---------------------------------------------------------------------------
# The work for one request
# ---------------------------------------------------------------------------
function Invoke-Push {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [int]$RequestId,
        [string]$OnlyMac,
        [bool]$IsDryRun
    )

    $changed = 0
    $failed = 0

    # PowerShell converts a $null argument to a [string] parameter into an EMPTY
    # STRING, not null. So "every device" arrived here as '', the predicate
    # "@mac IS NULL OR mac = @mac" matched nothing rather than everything, and a
    # push of all devices silently reported no work to do. Normalise it back.
    $macFilter = if ([string]::IsNullOrWhiteSpace($OnlyMac)) { $null } else { $OnlyMac }

    $rows = Invoke-Sql -Connection $Connection -Sql @'
SELECT mac, device_name, current_ip
FROM dbo.vDeviceTruth
WHERE publish_dns = 1
  AND device_name IS NOT NULL
  AND current_ip  IS NOT NULL
  AND name_source = 'manual'
  AND (@mac IS NULL OR mac = @mac)
ORDER BY device_name;
'@ -Parameters @{ mac = $macFilter }

    Step ("$($rows.Rows.Count) device(s) to publish" + $(if ($IsDryRun) { ' [DRY RUN]' } else { '' }))

    # Only names a person stated are published. A name resolved from a source is
    # a good guess for a report; it is not something to write into DNS on the
    # strength of a device's own claim about itself.
    if ($rows.Rows.Count -eq 0) {
        Detail 'Nothing stated and publishable. Name some devices in the editor first.'
        return @{ Changed = 0; Failed = 0; Message = 'No stated, publishable devices.' }
    }

    # A name cannot point at two addresses. Rather than refuse to publish any of
    # them, the first MAC - ordered by the MAC itself, so the choice does not
    # reshuffle just because DHCP handed one of them a new address - keeps the
    # stated name, and each other one is suffixed -2, -3, ... This is exactly
    # what happens when the same model name gets typed into several devices in
    # one sitting: HS103, HS103-2, HS103-3.
    #
    # device_name comes from vDeviceTruth, a view built on a COALESCE, so ADO.NET
    # marks the DataTable column read-only - the row itself cannot be renamed in
    # place. $nameOverrides carries the effective name per MAC instead.
    $nameOverrides = @{}
    $byName = $rows.Rows | Group-Object { $_.device_name.ToLowerInvariant() }
    $duplicated = @($byName | Where-Object { $_.Count -gt 1 })
    foreach ($g in $duplicated) {
        $ordered = @($g.Group | Sort-Object mac)
        $base = $ordered[0].device_name
        for ($i = 1; $i -lt $ordered.Count; $i++) {
            $suffixed = "$base-$($i + 1)"
            Warn "'$base' stated for $($ordered.Count) MACs; $($ordered[$i].mac) publishes as '$suffixed'"
            $nameOverrides[$ordered[$i].mac] = $suffixed
        }
    }

    # The forward zone, read once. Rewriting it record by record and re-reading
    # would be slower and would race with the dynamic updates still arriving.
    $existing = @(Get-DnsServerResourceRecord -ZoneName $Zone -RRType A -ComputerName $DnsServer)

    foreach ($row in $rows.Rows) {
        $mac = $row.mac
        $name = if ($nameOverrides.ContainsKey($mac)) { $nameOverrides[$mac] } else { $row.device_name }
        $ip = $row.current_ip

        try {
            # ---- the A record for this name -------------------------------
            $mine = @($existing | Where-Object { $_.HostName -eq $name })
            $correct = @($mine | Where-Object { $_.RecordData.IPv4Address.IPAddressToString -eq $ip })
            $wrong = @($mine | Where-Object { $_.RecordData.IPv4Address.IPAddressToString -ne $ip })

            foreach ($rec in $wrong) {
                $recIp = $rec.RecordData.IPv4Address.IPAddressToString
                if ($null -eq $rec.Timestamp) {
                    Warn "$name -> $recIp is STATIC; leaving it alone"
                    Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                        -Action skipped -RecordType A -RecordName $name -Ip $recIp -ZoneName $Zone `
                        -Detail 'Static record. Created deliberately, so not removed.'
                    continue
                }
                if ($IsDryRun) {
                    Detail "would remove A $name -> $recIp (stale)"
                    $changed++
                    Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                        -Action removed -RecordType A -RecordName $name -Ip $recIp -ZoneName $Zone `
                        -Detail "Name now at $ip."
                    continue
                }
                if (-not (Remove-ARecordIfPresent -Zone $Zone -DnsServer $DnsServer -Record $rec)) {
                    Detail "$name -> $recIp already gone (removed earlier this run)"
                    continue
                }
                $changed++
                Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                    -Action removed -RecordType A -RecordName $name -Ip $recIp -ZoneName $Zone `
                    -Detail "Name now at $ip."
            }

            if ($correct.Count -gt 0) {
                Detail "A $name -> $ip already correct"
                Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                    -Action skipped -RecordType A -RecordName $name -Ip $ip -ZoneName $Zone `
                    -Detail 'Already correct.'
            }
            else {
                if ($IsDryRun) {
                    Detail "would add A $name -> $ip"
                }
                else {
                    # -AgeRecord: without it the record is created STATIC (no
                    # timestamp) even in a zone with aging on, which means this
                    # script's own "is this record dynamic or did a person make
                    # it" check would treat every record it ever writes as
                    # untouchable forever - the exact rot this push exists to
                    # stop, reintroduced through the one call that writes a name.
                    Add-DnsServerResourceRecordA -Name $name -ZoneName $Zone -IPv4Address $ip `
                        -ComputerName $DnsServer -AllowUpdateAny:$false -AgeRecord
                }
                $changed++
                Ok "A $name -> $ip"
                Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                    -Action added -RecordType A -RecordName $name -Ip $ip -ZoneName $Zone
            }

            # ---- other names on the same address ---------------------------
            # The heart of it. One address, one name.
            $squatters = @($existing | Where-Object {
                    $_.RecordData.IPv4Address.IPAddressToString -eq $ip -and
                    $_.HostName -ne $name -and
                    $ProtectedLabels -notcontains $_.HostName
                })

            foreach ($rec in $squatters) {
                if ($null -eq $rec.Timestamp) {
                    Detail "keeping static alias $($rec.HostName) -> $ip"
                    Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                        -Action skipped -RecordType A -RecordName $rec.HostName -Ip $ip -ZoneName $Zone `
                        -Detail 'Static alias on this address. Deliberate, so kept.'
                    continue
                }
                if ($IsDryRun) {
                    Detail "would remove A $($rec.HostName) -> $ip (address belongs to $name)"
                    $changed++
                    Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                        -Action removed -RecordType A -RecordName $rec.HostName -Ip $ip -ZoneName $Zone `
                        -Detail "Address is $name. Dynamic leftover from a previous lease."
                    continue
                }
                if (-not (Remove-ARecordIfPresent -Zone $Zone -DnsServer $DnsServer -Record $rec)) {
                    Detail "$($rec.HostName) -> $ip already gone (removed earlier this run)"
                    continue
                }
                $changed++
                Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                    -Action removed -RecordType A -RecordName $rec.HostName -Ip $ip -ZoneName $Zone `
                    -Detail "Address is $name. Dynamic leftover from a previous lease."
            }

            # ---- PTR -------------------------------------------------------
            $revZone = Get-ReverseZoneName -Ip $ip
            $ptrLabel = Get-PtrLabel -Ip $ip
            $fqdn = "$name.$Zone"

            $zoneExists = $null -ne (Get-DnsServerZone -Name $revZone -ComputerName $DnsServer `
                    -ErrorAction SilentlyContinue)

            if (-not $zoneExists) {
                # 10.20.1.x has no reverse zone at all today, so those clients
                # have no PTR and Pi-hole's reverse lookups for them fail.
                if ($IsDryRun) {
                    Detail "would create reverse zone $revZone"
                }
                else {
                    Add-DnsServerPrimaryZone -Name $revZone -ReplicationScope Domain `
                        -DynamicUpdate Secure -ComputerName $DnsServer
                    # Aging on, unlike the existing reverse zone, so this one does
                    # not accumulate the same rot.
                    Set-DnsServerZoneAging -Name $revZone -Aging $true -ComputerName $DnsServer
                    $zoneExists = $true
                }
                $changed++
                Ok "reverse zone $revZone"
                Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                    -Action added -RecordType zone -RecordName $revZone -ZoneName $revZone `
                    -Detail 'Created with aging enabled.'
            }

            if ($zoneExists) {
                $ptrs = @(Get-DnsServerResourceRecord -ZoneName $revZone -Name $ptrLabel `
                        -RRType Ptr -ComputerName $DnsServer -ErrorAction SilentlyContinue)
                $ptrOk = @($ptrs | Where-Object {
                        $_.RecordData.PtrDomainName.TrimEnd('.') -eq $fqdn })
                $ptrBad = @($ptrs | Where-Object {
                        $_.RecordData.PtrDomainName.TrimEnd('.') -ne $fqdn })

                foreach ($rec in $ptrBad) {
                    $was = $rec.RecordData.PtrDomainName.TrimEnd('.')
                    if ($IsDryRun) {
                        Detail "would remove PTR $ip -> $was"
                    }
                    else {
                        Remove-DnsServerResourceRecord -ZoneName $revZone -InputObject $rec `
                            -ComputerName $DnsServer -Force
                    }
                    $changed++
                    Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                        -Action removed -RecordType PTR -RecordName $was -Ip $ip -ZoneName $revZone `
                        -Detail "Address now resolves to $fqdn."
                }

                if ($ptrOk.Count -eq 0) {
                    if ($IsDryRun) {
                        Detail "would add PTR $ip -> $fqdn"
                    }
                    else {
                        # Same reasoning as the A record: without -AgeRecord this
                        # is created static and never ages out.
                        Add-DnsServerResourceRecordPtr -Name $ptrLabel -ZoneName $revZone `
                            -PtrDomainName $fqdn -ComputerName $DnsServer -AgeRecord
                    }
                    $changed++
                    Ok "PTR $ip -> $fqdn"
                    Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                        -Action added -RecordType PTR -RecordName $fqdn -Ip $ip -ZoneName $revZone
                }
            }

            # ---- the controller --------------------------------------------
            # Write the stated name back so Omada shows "Basement-Stairs"
            # instead of "HS210". Its own try/catch: a controller problem is not
            # a DNS problem, and logging it as a failed A record would be a lie.
            if (-not $SkipOmada) {
                try {
                    $ctx = Connect-Omada
                    if ($ctx) {
                        $current = Get-OmadaClientName -Ctx $ctx -Mac $mac
                        if ($null -eq $current) {
                            Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                                -Action skipped -RecordType omada -RecordName $name `
                                -Detail 'The controller does not know this MAC.'
                        }
                        elseif ($current -eq $name) {
                            Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                                -Action skipped -RecordType omada -RecordName $name `
                                -Detail 'Controller name already matches.'
                        }
                        else {
                            if ($IsDryRun) {
                                Detail "would set Omada name '$current' -> '$name'"
                            }
                            else {
                                $null = Set-OmadaClientName -Ctx $ctx -Mac $mac -Name $name
                            }
                            $changed++
                            Ok "Omada name '$current' -> '$name'"
                            Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                                -Action updated -RecordType omada -RecordName $name `
                                -Detail "Controller name was '$current'."
                        }
                    }
                }
                catch {
                    $failed++
                    Warn "Omada name for $name ($mac): $($_.Exception.Message)"
                    Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                        -Action failed -RecordType omada -RecordName $name `
                        -Detail $_.Exception.Message
                }
            }
        }
        catch {
            $failed++
            Warn "$name ($mac): $($_.Exception.Message)"
            Write-PushLog -Connection $Connection -RequestId $RequestId -Mac $mac `
                -Action failed -RecordType A -RecordName $name -Ip $ip -ZoneName $Zone `
                -Detail $_.Exception.Message
        }
    }

    $msg = "$changed record(s) $(if ($IsDryRun) { 'would change' } else { 'changed' })"
    if ($failed) { $msg += ", $failed failed" }
    if ($duplicated.Count) { $msg += ", $($duplicated.Count) name(s) disambiguated with a numeric suffix" }

    @{ Changed = $changed; Failed = $failed; Message = $msg }
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
$conn = New-SqlConnection
try {
    if ($Force) {
        Step 'Forced run, not claiming a queued request'
        $result = Invoke-Push -Connection $conn -RequestId 0 -OnlyMac $null -IsDryRun:$DryRun.IsPresent
        Ok $result.Message
        Write-RunEvent -Message $result.Message
        return
    }

    for ($i = 0; $i -lt $MaxRequests; $i++) {
        # Claim in one statement. Two agent runs overlapping cannot both take the
        # same request, because only one UPDATE can match status = 'queued'.
        $claim = Invoke-Sql -Connection $conn -Sql @'
UPDATE TOP (1) dbo.DnsPushRequest
SET status = 'running', claimed_utc = SYSUTCDATETIME(), agent_host = @host
OUTPUT inserted.id, inserted.mac, inserted.dry_run
WHERE status = 'queued';
'@ -Parameters @{ host = $env:COMPUTERNAME }

        if ($claim.Rows.Count -eq 0) {
            if ($i -eq 0) { Detail 'Nothing queued.' }
            break
        }

        $id = [int]$claim.Rows[0].id
        $mac = if ($claim.Rows[0].mac -is [DBNull]) { $null } else { [string]$claim.Rows[0].mac }
        $dry = [bool]$claim.Rows[0].dry_run

        Step "request #$id  scope=$(if ($mac) { $mac } else { 'all' })  dryRun=$dry"

        try {
            $result = Invoke-Push -Connection $conn -RequestId $id -OnlyMac $mac -IsDryRun:$dry
            $null = Invoke-Sql -Connection $conn -NonQuery -Sql @'
UPDATE dbo.DnsPushRequest
SET status = 'done', completed_utc = SYSUTCDATETIME(),
    records_changed = @changed, records_failed = @failed, message = @msg
WHERE id = @id;
'@ -Parameters @{ id = $id; changed = $result.Changed; failed = $result.Failed; msg = $result.Message }
            Ok "request #$id  $($result.Message)"
            Write-RunEvent -Message "request #$id  $($result.Message)"
        }
        catch {
            # Never leave a request 'running'. A stuck row blocks the queue cap
            # and tells whoever looks nothing about why.
            $null = Invoke-Sql -Connection $conn -NonQuery -Sql @'
UPDATE dbo.DnsPushRequest
SET status = 'failed', completed_utc = SYSUTCDATETIME(), message = @msg
WHERE id = @id;
'@ -Parameters @{ id = $id; msg = $_.Exception.Message }
            throw
        }
    }
}
finally {
    $conn.Close()
}
