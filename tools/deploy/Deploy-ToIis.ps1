<#
.SYNOPSIS
    Publishes the report server and deploys it to the IIS host.

.DESCRIPTION
    Run from a workstation that can reach the web host's admin share and manage its
    IIS. Encodes three things that have each broken a deploy here:

      1. The app pool must be STOPPED AND CONFIRMED STOPPED before copying. A
         running in-process worker holds PiholeReportServer.dll open, robocopy
         reports exit 11, and the files silently do not copy - so the old build
         keeps serving and the deploy looks successful.

      2. robocopy exit codes below 8 are success (1 = copied, 2 = extra, 3 = both).
         8 and above mean files were NOT copied. Anything that treats non-zero as
         failure, or zero as the only success, gets this backwards.

      3. C:\inetpub\sites does not pass IIS_IUSRS down to a new child directory, and
         /MIR resets inherited ACLs anyway. Without re-asserting the grant after
         every copy the site returns HTTP 500.19 with win32 status 5.

    App pool environment variables are NOT touched: they hold the real secrets and
    endpoints, they survive a redeploy, and appsettings.json ships with empty
    placeholders on purpose.

.PARAMETER Server
    IIS host name.

.PARAMETER AppPool
    App pool name. Also used to find the site's physical path.

.PARAMETER SitePath
    Physical path of the site ON the server.

.PARAMETER SkipTests
    Deploy without running the test suite first. Not recommended.

.EXAMPLE
    .\Deploy-ToIis.ps1
    .\Deploy-ToIis.ps1 -Server WINSERVER03 -AppPool PiholeReportServer
#>
[CmdletBinding()]
param(
    [string]$Server   = 'WINSERVER03',
    [string]$AppPool  = 'PiholeReportServer',
    [string]$SitePath = 'C:\inetpub\sites\status',
    [string]$Url      = 'https://status.bylotas.com/',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  OK   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  WARN $m" -ForegroundColor Yellow }

$repo    = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repo 'src\PiholeReportServer\PiholeReportServer.csproj'
$staging = Join-Path $env:TEMP "piholereport-publish-$(Get-Date -Format yyyyMMddHHmmss)"
$unc     = "\\$Server\$($SitePath -replace ':','$')"

# ── 1. Build and test ──────────────────────────────────────────────────────
if (-not $SkipTests) {
    Step 'Tests'
    dotnet test $repo --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. Not deploying.' }
    Ok 'suite green'
}

Step 'Publish'
dotnet publish $project -c Release -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

# The build does not parse appsettings.json, and a malformed one has taken the site
# down here before while the build stayed green.
$appsettings = Join-Path $staging 'appsettings.json'
try { [void](Get-Content $appsettings -Raw | ConvertFrom-Json); Ok 'appsettings.json is valid JSON' }
catch { throw "appsettings.json in the publish output is not valid JSON: $($_.Exception.Message)" }
Ok "published to $staging ($((Get-ChildItem $staging -Recurse -File).Count) files)"

# ── 2. Stop the pool, and CONFIRM it stopped ───────────────────────────────
Step "Stopping $AppPool on $Server"

$stopped = Invoke-Command -ComputerName $Server -ArgumentList $AppPool -ScriptBlock {
    param($pool)
    Import-Module WebAdministration
    if ((Get-WebAppPoolState -Name $pool).Value -ne 'Stopped') {
        Stop-WebAppPool -Name $pool
    }
    # Requesting a stop returns immediately; the worker keeps the DLL open until it
    # actually exits. Copying during that window is the failure this waits out.
    foreach ($i in 1..40) {
        if ((Get-WebAppPoolState -Name $pool).Value -eq 'Stopped') { return $true }
        Start-Sleep -Milliseconds 500
    }
    return (Get-WebAppPoolState -Name $pool).Value -eq 'Stopped'
}
if (-not $stopped) { throw "$AppPool did not reach Stopped within 20s. Not copying." }
Ok 'pool confirmed Stopped'

# ── 3. Copy ────────────────────────────────────────────────────────────────
Step 'Copying'

robocopy $staging $unc /MIR /NFL /NDL /NJH /NJS /NP /R:2 /W:2 | Out-Null
$rc = $LASTEXITCODE
# 0 = nothing to do, 1 = copied, 2 = extras removed, 3 = both. 8+ = real failure.
if ($rc -ge 8) {
    Warn "robocopy exit $rc - files were NOT copied. Starting the pool again so the old build serves."
    Invoke-Command -ComputerName $Server -ArgumentList $AppPool -ScriptBlock {
        param($pool); Import-Module WebAdministration; Start-WebAppPool -Name $pool
    }
    throw "robocopy failed with exit $rc."
}
Ok "copied (robocopy exit $rc)"

# ── 4. Re-assert the ACL, every time ───────────────────────────────────────
Step 'Permissions'

Invoke-Command -ComputerName $Server -ArgumentList $SitePath -ScriptBlock {
    param($path)
    # /MIR resets inherited ACLs, and C:\inetpub\sites does not pass IIS_IUSRS down
    # to its children. Skipping this yields HTTP 500.19, win32 status 5.
    & icacls $path /grant 'BUILTIN\IIS_IUSRS:(OI)(CI)(RX)' /T /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed with exit $LASTEXITCODE" }
}
Ok 'IIS_IUSRS granted read/execute'

# ── 4b. Event log ──────────────────────────────────────────────────────────
Step 'Event log'

# The app logs to the Windows event log, and registering a source needs
# administrator - which the app pool is not. If the source is missing the provider
# drops messages silently rather than failing to start, so a host that was never
# registered looks healthy and logs nothing. Do it on every deploy; it is a no-op
# once it exists.
& (Join-Path $PSScriptRoot 'Register-EventLog.ps1') -ComputerName $Server

# ── 5. Start and verify ────────────────────────────────────────────────────
Step 'Starting'

$state = Invoke-Command -ComputerName $Server -ArgumentList $AppPool -ScriptBlock {
    param($pool)
    Import-Module WebAdministration
    Start-WebAppPool -Name $pool
    foreach ($i in 1..20) {
        if ((Get-WebAppPoolState -Name $pool).Value -eq 'Started') { break }
        Start-Sleep -Milliseconds 500
    }
    (Get-WebAppPoolState -Name $pool).Value
}
Ok "pool state: $state"

Step 'Verify'

# An unauthenticated request must redirect to Entra. A 500 here means the app threw
# on startup - check the Application event log on the server.
$code = Invoke-Command -ComputerName $Server -ArgumentList $Url -ScriptBlock {
    param($url)
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.AllowAutoRedirect = $false
        $req.Timeout = 30000
        $resp = $req.GetResponse()
        $c = [int]$resp.StatusCode
        $resp.Close()
        $c
    } catch [System.Net.WebException] {
        if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
    }
}
if ($code -eq 302) {
    Ok "$Url returned 302 to Entra"
} elseif ($code -eq -1) {
    Warn "could not reach $Url at all"
} else {
    Warn "$Url returned $code - expected 302. Check the Application event log on $Server."
}

$errors = Invoke-Command -ComputerName $Server -ScriptBlock {
    Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-3)} `
        -ErrorAction SilentlyContinue |
      Where-Object { $_.LevelDisplayName -eq 'Error' -and $_.Message -match 'Pihole|AspNetCore' } |
      Select-Object -First 3 -ExpandProperty Message
}
if ($errors) {
    Warn 'errors in the Application log since the restart:'
    $errors | ForEach-Object { Write-Host "         $($_.Substring(0, [Math]::Min(200, $_.Length)))" }
} else {
    Ok 'no application errors logged'
}

Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
Step 'Done'

# robocopy leaves $LASTEXITCODE at 1-3 on success, and PowerShell would otherwise
# return that as this script's exit code, which a CI wrapper reads as failure.
exit 0
