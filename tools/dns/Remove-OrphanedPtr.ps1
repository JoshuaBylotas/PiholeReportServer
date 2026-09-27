<#
.SYNOPSIS
    Removes PTR records whose name does not exist in the forward zone.

.DESCRIPTION
    Pi-hole names clients by reverse lookup, so a wrong PTR becomes a wrong
    device name in every report and on the dashboard. The reverse zone had
    accumulated years of them: of 135 PTR records, only 25 were
    forward-confirmed.

    This removes the ORPHANS - a PTR whose target has no A record at all, which
    makes it unfalsifiable junk rather than a disagreement:

        ALIEN01.bylotas.net        45 addresses, and no A record anywhere
        WINSERVER02.bylotas.net     7
        WINSERVER04.bylotas.net     7
        play.inpvp.net              a Minecraft stub zone, in the reverse zone
        myChevrolet, linserver99, ubuntu, Tstat-6FF068, ...

    Unlike the MAC-shaped purge, the static/dynamic distinction is not used and
    could not be: aging is off on this zone, so most records here carry no
    timestamp whether they are junk or not.

    WHAT IT DOES NOT TOUCH

      * A forward-confirmed PTR - the name resolves back to this address. Both
        NICs of a multi-homed host survive, because both are confirmed.
      * A MISMATCHED PTR - the name exists but resolves to a different address
        (7 stale OMEN01 entries, 4 HBG-BYLOTASJ4.tcore.com, 2 WINAD02). Those
        are a disagreement between two real records, and picking a winner is a
        judgement. Left deliberately; the push agent resolves them per device as
        each one is named.

    Only A records are consulted when deciding whether a name exists. A PTR
    pointing at a CNAME would be treated as orphaned - there are none here, but
    it is worth knowing before running this on another zone.

    DRY RUN BY DEFAULT. Nothing is deleted without -Apply.

.EXAMPLE
    .\Remove-OrphanedPtr.ps1
    .\Remove-OrphanedPtr.ps1 -Apply
#>
[CmdletBinding()]
param(
    [string]$DnsServer   = 'WINAD02',
    [string]$ForwardZone = 'bylotas.net',
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Import-Module DnsServer -ErrorAction Stop

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  OK   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  WARN $m" -ForegroundColor Yellow }

$mode = if ($Apply) { 'APPLY' } else { 'DRY RUN' }
Step "$mode - orphaned PTR records, resolved against $ForwardZone"

# Every name that exists forward, with the addresses it holds.
$forward = @{}
foreach ($r in @(Get-DnsServerResourceRecord -ZoneName $ForwardZone -RRType A -ComputerName $DnsServer)) {
    $n = $r.HostName.ToLowerInvariant()
    if (-not $forward.ContainsKey($n)) { $forward[$n] = New-Object System.Collections.ArrayList }
    [void]$forward[$n].Add($r.RecordData.IPv4Address.IPAddressToString)
}
Write-Host "  $($forward.Count) distinct name(s) exist in the forward zone"

$revZones = @(Get-DnsServerZone -ComputerName $DnsServer |
              Where-Object { $_.IsReverseLookupZone -and -not $_.IsAutoCreated })

$removed = 0
$failed = 0
$keptConfirmed = 0
$keptMismatch = 0
$orphans = New-Object System.Collections.ArrayList

foreach ($z in $revZones) {
    # 0.20.10.in-addr.arpa -> 10.20.0 ; used to rebuild the address a PTR is for.
    $parts = $z.ZoneName -replace '\.in-addr\.arpa$', '' -split '\.'
    [array]::Reverse($parts)
    $prefix = $parts -join '.'

    $ptrs = @(Get-DnsServerResourceRecord -ZoneName $z.ZoneName -RRType Ptr `
                -ComputerName $DnsServer -ErrorAction SilentlyContinue)
    Write-Host "  $($z.ZoneName): $($ptrs.Count) PTR record(s)"

    foreach ($p in $ptrs) {
        $addr = "$prefix.$($p.HostName)"
        $target = $p.RecordData.PtrDomainName
        $label = ($target -split '\.')[0].ToLowerInvariant()

        if ($forward.ContainsKey($label)) {
            if ($forward[$label] -contains $addr) { $keptConfirmed++ }
            else { $keptMismatch++ }
            continue
        }

        [void]$orphans.Add([pscustomobject]@{ Zone = $z.ZoneName; IP = $addr; Target = $target; Record = $p })
    }
}

Step "Orphans found: $($orphans.Count)"
# Write-Host, not the pipeline: Step/Ok/Warn write straight to the host, so
# pipeline output is buffered and arrives AFTER the summary instead of under
# this heading. Same trap CollectorLogging.ps1 documents.
$orphans | Group-Object Target | Sort-Object Count -Descending | ForEach-Object {
    Write-Host ("  {0,-42} {1,3}  {2}" -f $_.Name, $_.Count, (($_.Group.IP | Sort-Object) -join ', '))
}

if ($Apply) {
    Step 'Removing'
    foreach ($o in $orphans) {
        try {
            Remove-DnsServerResourceRecord -ZoneName $o.Zone -InputObject $o.Record `
                -ComputerName $DnsServer -Force
            $removed++
        }
        catch {
            $failed++
            Warn "failed $($o.IP) -> $($o.Target): $($_.Exception.Message)"
        }
    }
}

Step 'Summary'
"  forward-confirmed, kept : $keptConfirmed"
"  mismatched, kept        : $keptMismatch  (deliberate - see the description)"
if ($Apply) {
    Ok "removed $removed orphaned PTR record(s)"
    if ($failed) { Warn "$failed failure(s)" }
    Write-Host ''
    Write-Host '  Next: clear Pi-hole cached names, or it keeps serving the old ones:' -ForegroundColor Yellow
    Write-Host '    sudo systemctl stop pihole-FTL' -ForegroundColor Yellow
    Write-Host "    sudo pihole-FTL sqlite3 /etc/pihole/pihole-FTL.db 'UPDATE network_addresses SET name=NULL, nameUpdated=0;'" -ForegroundColor Yellow
    Write-Host '    sudo systemctl start pihole-FTL' -ForegroundColor Yellow
}
else {
    "  would remove            : $($orphans.Count)"
    Write-Host ''
    Write-Host '  Nothing was changed. Re-run with -Apply to delete.' -ForegroundColor Yellow
}
