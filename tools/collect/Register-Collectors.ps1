<#
.SYNOPSIS
    Installs the device-name collectors on the DC and schedules them daily.

.DESCRIPTION
    Mirrors how the Pi-hole import is scheduled on linserver05 - a oneshot job on a
    daily timer with Persistent=true, so a machine that was off catches up rather
    than silently skipping a day. The Windows equivalent of Persistent is
    StartWhenAvailable, which this sets.

    WHY ON THE DOMAIN CONTROLLER

    Both sources live there. WINAD02 runs AD DNS, and the Omada controller is on the
    same host (firewall.bylotas.net resolves to it). Running the collectors there
    means:

      * no WinRM hop to read the zone - the DNS role is local
      * no network call to reach the controller - it is on localhost
      * no stored password: the tasks run as SYSTEM and authenticate to SQL as the
        computer account, which has been granted SELECT on DimClient and CRUD on
        DeviceNameObservation and nothing else

    The alternative - running on WINSERVER01 - needs either a service account
    password on disk or DNS read rights granted to another computer account. This
    was the smaller ask. If you would rather not schedule work on a DC, pass a
    different -ComputerName; you will then need to sort out those rights.

    The Omada secret is copied to the DC, which is already the machine hosting the
    controller, and the file is ACLed to SYSTEM and Administrators only.

.PARAMETER ComputerName
    Where to install. Must be able to read the DNS zone and reach the controller.

.PARAMETER InstallPath
    Directory on that machine to hold the scripts.

.PARAMETER AtTime
    Daily run time, local to that machine. Defaults to 00:30 - after the Pi-hole
    dimension refresh at midnight, so DimClient already has the night's MAC/IP
    pairings when the collectors join against it.

.PARAMETER Uninstall
    Remove the tasks and the installed files.

.EXAMPLE
    .\Register-Collectors.ps1
    .\Register-Collectors.ps1 -AtTime 02:00
    .\Register-Collectors.ps1 -Uninstall
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ComputerName = 'WINAD02',
    [string]$InstallPath  = 'C:\Scripts\PiholeCollect',
    [string]$AtTime       = '00:30',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "  OK   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  WARN $m" -ForegroundColor Yellow }

$here   = $PSScriptRoot
$repo   = Split-Path -Parent (Split-Path -Parent $here)
$scripts = @('Collect-AdDnsNames.ps1', 'Collect-OmadaNames.ps1')
$credSrc = Join-Path $repo 'private\omada.json'

$TaskNames = @{
    'Collect-AdDnsNames.ps1' = 'PiholeReport-CollectAdDnsNames'
    'Collect-OmadaNames.ps1' = 'PiholeReport-CollectOmadaNames'
}

# ── Uninstall ───────────────────────────────────────────────────────────────
if ($Uninstall) {
    Step "Removing from $ComputerName"
    Invoke-Command -ComputerName $ComputerName -ArgumentList $InstallPath, ($TaskNames.Values -join ',') -ScriptBlock {
        param($path, $tasks)
        foreach ($t in $tasks -split ',') {
            if (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) {
                Unregister-ScheduledTask -TaskName $t -Confirm:$false
                "  removed task $t"
            }
        }
        if (Test-Path $path) { Remove-Item $path -Recurse -Force; "  removed $path" }
    }
    Ok 'uninstalled'
    return
}

# ── Preflight ───────────────────────────────────────────────────────────────
Step 'Checking the target'

foreach ($s in $scripts) {
    if (-not (Test-Path (Join-Path $here $s))) { throw "Missing $s in $here" }
}
if (-not (Test-Path $credSrc)) {
    throw "No Omada credentials at $credSrc. See Collect-OmadaNames.ps1 for how to create them."
}

$ready = Invoke-Command -ComputerName $ComputerName -ScriptBlock {
    [pscustomobject]@{
        Host      = $env:COMPUTERNAME
        DnsModule = [bool](Get-Module -ListAvailable -Name DnsServer)
        PsVersion = $PSVersionTable.PSVersion.ToString()
        # NOT tested here. This session reached the machine over WinRM, so a second
        # hop to SQL arrives as ANONYMOUS LOGON - the classic double hop. It would
        # fail regardless of whether the scheduled task can connect, because the
        # task runs locally as SYSTEM and authenticates as the computer account
        # with a real ticket. The test run at the end is what actually proves it.
        Sql       = 'not testable from here - see the test run below'
    }
}
"  host:            $($ready.Host)"
"  PowerShell:      $($ready.PsVersion)"
"  DnsServer module: $(if ($ready.DnsModule) { 'present' } else { 'MISSING' })"
"  SQL:             $($ready.Sql)"

if (-not $ready.DnsModule) {
    throw "$ComputerName has no DnsServer module, so it cannot read the zone locally."
}

# ── Copy ────────────────────────────────────────────────────────────────────
Step 'Installing files'

$unc = "\\$ComputerName\$($InstallPath -replace ':','$')"
if (-not (Test-Path $unc)) { New-Item -ItemType Directory -Path $unc -Force | Out-Null }
foreach ($s in $scripts) { Copy-Item (Join-Path $here $s) $unc -Force }
Copy-Item $credSrc (Join-Path $unc 'omada.json') -Force
Ok "copied $($scripts.Count) script(s) and the credential file to $InstallPath"

# The credential file holds the controller secret. SYSTEM runs the task; nobody
# else needs to read it.
Invoke-Command -ComputerName $ComputerName -ArgumentList $InstallPath -ScriptBlock {
    param($path)
    $f = Join-Path $path 'omada.json'
    $acl = New-Object System.Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)     # drop inheritance
    foreach ($who in 'NT AUTHORITY\SYSTEM', 'BUILTIN\Administrators') {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $who, 'FullControl', 'Allow')))
    }
    $acl.SetOwner([System.Security.Principal.NTAccount]'BUILTIN\Administrators')
    Set-Acl -Path $f -AclObject $acl
}
Ok 'credential file restricted to SYSTEM and Administrators'

# ── Schedule ────────────────────────────────────────────────────────────────
Step 'Registering tasks'

$results = Invoke-Command -ComputerName $ComputerName `
    -ArgumentList $InstallPath, $AtTime, ($TaskNames.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -ScriptBlock {
    param($path, $at, $pairs)

    $out = @()
    $i = 0
    foreach ($pair in $pairs) {
        $script, $task = $pair -split '=', 2

        # Stagger by two minutes. Both write to the same table and the AD DNS one
        # reads DimClient; there is no lock contention to speak of, but a clean
        # order makes a failure easier to read in the log.
        $when = ([datetime]::Parse($at)).AddMinutes(2 * $i)
        $i++

        # No -Confirm:$false here. With -File, powershell.exe passes arguments as
        # literal STRINGS, so "-Confirm:$false" arrives as the string '$false' and
        # cannot bind to a switch: "Cannot convert 'System.String' to the type
        # 'SwitchParameter'". The script then fails before it runs a single line,
        # which is why the first attempt produced no transcript to diagnose from.
        # It is not needed anyway - ShouldProcess does not prompt at the default
        # ConfirmPreference, and the task runs -NonInteractive regardless.
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
            -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$path\$script`"" `
            -WorkingDirectory $path

        $trigger = New-ScheduledTaskTrigger -Daily -At $when

        # StartWhenAvailable is the Persistent=true of the systemd timer that runs
        # the Pi-hole import: a machine that was off runs the job late rather than
        # skipping the day entirely.
        $settings = New-ScheduledTaskSettingsSet `
            -StartWhenAvailable `
            -DontStopIfGoingOnBatteries -AllowStartIfOnBatteries `
            -ExecutionTimeLimit (New-TimeSpan -Minutes 30) `
            -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 5) `
            -MultipleInstances IgnoreNew

        # SYSTEM, so there is no password to store or rotate. It reaches SQL as the
        # computer account, which has been granted only what these need.
        $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' `
            -LogonType ServiceAccount -RunLevel Highest

        Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger `
            -Settings $settings -Principal $principal -Force `
            -Description 'Collects device names for the Pi-hole Report Server warehouse.' | Out-Null

        $out += [pscustomobject]@{ Task = $task; At = $when.ToString('HH:mm') }
    }
    $out
}
$results | ForEach-Object { Ok "$($_.Task) daily at $($_.At)" }

# ── Prove it works ──────────────────────────────────────────────────────────
Step 'Test run'

$run = Invoke-Command -ComputerName $ComputerName -ArgumentList ($TaskNames.Values -join ',') -ScriptBlock {
    param($tasks)
    $out = @()
    foreach ($t in $tasks -split ',') {
        Start-ScheduledTask -TaskName $t
        # Wait for it to finish rather than reporting "started" and hoping.
        $waited = 0
        while ((Get-ScheduledTask -TaskName $t).State -eq 'Running' -and $waited -lt 300) {
            Start-Sleep -Seconds 5; $waited += 5
        }
        $info = Get-ScheduledTaskInfo -TaskName $t
        $out += [pscustomobject]@{
            Task    = $t
            Result  = $info.LastTaskResult
            LastRun = $info.LastRunTime
            Seconds = $waited
        }
    }
    $out
}

$failed = 0
foreach ($r in $run) {
    if ($r.Result -eq 0) {
        Ok "$($r.Task) succeeded in ~$($r.Seconds)s"
    } else {
        Warn "$($r.Task) exited with $($r.Result) - check Task Scheduler history on $ComputerName"
        $failed++
    }
}

Step 'Effect'
$conn = New-Object System.Data.SqlClient.SqlConnection(
    'Server=WINSERVER01;Database=pihole;Integrated Security=true;TrustServerCertificate=true')
$conn.Open()
$cmd = $conn.CreateCommand()
$cmd.CommandText = @'
SELECT source, macs = COUNT(*), newest = MAX(observed_utc)
FROM dbo.DeviceNameObservation GROUP BY source ORDER BY COUNT(*) DESC;
'@
$r = $cmd.ExecuteReader()
while ($r.Read()) { "  {0,-10} {1,4} MAC(s), newest {2:u}" -f $r['source'], $r['macs'], $r['newest'] }
$r.Close()
$conn.Close()

if ($failed -gt 0) { exit 1 }
Write-Host "`n  Both collectors will run daily. Nothing to maintain: they replace their" -ForegroundColor Cyan
Write-Host "  own rows each time, so upstream deletions take effect on the next run." -ForegroundColor Cyan
exit 0
