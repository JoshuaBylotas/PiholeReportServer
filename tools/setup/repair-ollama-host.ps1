<#
.SYNOPSIS
    Repairs an Ollama GPU host where a second, pre-existing server is shadowing
    the one this project configured.

.DESCRIPTION
    Run this ON the GPU machine, in an ELEVATED PowerShell window.

    THE FAULT THIS FIXES

    Windows lets two processes listen on the same TCP port when one binds the
    IPv6 wildcard and the other binds IPv4 loopback:

        ::           11434     <- ollama serve with OLLAMA_HOST=0.0.0.0
        127.0.0.1    11434     <- a second ollama serve on stock defaults

    Neither process fails to bind, so neither reports a problem. Every local
    client - "ollama pull", "ollama ps", a curl to 127.0.0.1 - reaches the
    loopback server. A remote client arriving over IPv4 reaches the wildcard
    one. So the model downloads into one server's store and the report server
    talks to the other, which answers /api/tags with {"models":[]} and 404s
    every generate request.

    It presents as "the install worked perfectly and the port is open, but the
    remote host says the model does not exist".

    WHAT THIS DOES

      1. Reports every ollama process and which socket each one owns, so the
         diagnosis is visible rather than asserted.
      2. Stops all of them, including any desktop-app tray instance.
      3. Finds the model blobs already on disk and moves them into
         OLLAMA_MODELS, so a 9 GB model is not downloaded twice.
      4. Starts only the scheduled task, then verifies the things that actually
         matter: the listener is off-loopback, the model is listed, keep-alive
         took effect, and inference still lands on the GPU.

.PARAMETER Model
    The model that should be present when this finishes. Pulled only if the
    blobs cannot be found on disk.

.EXAMPLE
    .\repair-ollama-host.ps1
#>
[CmdletBinding()]
param(
    [string]$Model = 'qwen2.5:14b-instruct',
    [string]$InstallDir = 'C:\Ollama'
)

$ErrorActionPreference = 'Stop'

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "  OK   $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "  WARN $msg" -ForegroundColor Yellow }
function Bad($msg)  { Write-Host "  FAIL $msg" -ForegroundColor Red }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this in an elevated PowerShell window (Run as Administrator).'
}

$exe        = Join-Path $InstallDir 'ollama.exe'
$modelsPath = Join-Path $InstallDir 'models'
if (-not (Test-Path $exe)) { throw "$exe not found. Run install-ollama-gpu.ps1 first." }

# ── 1. Show what is actually running ───────────────────────────────────────
Step 'Before: what holds port 11434'

$listeners = @(Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction SilentlyContinue)
if ($listeners.Count -eq 0) {
    Warn 'nothing is listening on 11434'
} else {
    foreach ($l in $listeners) {
        $p    = Get-Process -Id $l.OwningProcess -ErrorAction SilentlyContinue
        $cim  = Get-CimInstance Win32_Process -Filter "ProcessId = $($l.OwningProcess)" -ErrorAction SilentlyContinue
        $user = if ($cim) { try { ($cim | Invoke-CimMethod -MethodName GetOwner).User } catch { '?' } } else { '?' }
        Write-Host ("  {0,-14} pid {1,-6} {2,-14} user={3}  path={4}" -f `
            $l.LocalAddress, $l.OwningProcess, $(if ($p) { $p.ProcessName } else { '?' }), `
            $user, $(if ($cim) { $cim.ExecutablePath } else { '?' }))
    }
    if ($listeners.Count -gt 1) {
        Warn "$($listeners.Count) listeners on one port - this is the fault described in this script's header"
    }
}

# Reachability from off-box is what actually matters, and it is the thing the
# local tests cannot tell you. A wildcard listener is the requirement.
$offLoopback = $listeners | Where-Object { $_.LocalAddress -in '0.0.0.0', '::' }
if (-not $offLoopback -and $listeners.Count -gt 0) {
    Warn 'no wildcard listener: nothing off this machine can connect at all'
}

Step 'Before: ollama processes'
$procs = @(Get-Process -Name 'ollama*' -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) {
    Write-Host '  none'
} else {
    $procs | ForEach-Object {
        $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($_.Id)" -ErrorAction SilentlyContinue
        Write-Host ("  pid {0,-6} {1,-16} {2}" -f $_.Id, $_.ProcessName,
                    $(if ($cim) { $cim.CommandLine } else { '' }))
    }
}

# ── 2. Stop everything ─────────────────────────────────────────────────────
Step 'Stopping every Ollama process'

# The scheduled task first, so its restart policy does not race us.
if (Get-ScheduledTask -TaskName 'Ollama' -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName 'Ollama' -ErrorAction SilentlyContinue
    Ok 'scheduled task stopped'
}

# "ollama app.exe" is the desktop tray build - a likely source of the second
# server, and it respawns "ollama.exe serve" if it is left running.
Get-Process -Name 'ollama app', 'ollama_llama_server', 'ollama' -ErrorAction SilentlyContinue |
    ForEach-Object {
        # Capture these first: stopping the scheduled task above may already have
        # taken this process down, and an exited object no longer reports its own
        # Id, which turned the failure message into "could not kill pid :".
        $pid_ = $_.Id; $pname = $_.ProcessName
        if ($_.HasExited) { Ok "pid $pid_ ($pname) already exited with the task"; return }
        try { Stop-Process -Id $pid_ -Force -ErrorAction Stop; Ok "killed pid $pid_ ($pname)" }
        catch {
            if (-not (Get-Process -Id $pid_ -ErrorAction SilentlyContinue)) {
                Ok "pid $pid_ ($pname) exited on its own"
            } else {
                Warn "could not kill pid $pid_ ($pname): $($_.Exception.Message)"
            }
        }
    }

foreach ($i in 1..15) {
    Start-Sleep -Milliseconds 500
    if (-not (Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction SilentlyContinue)) { break }
}
if (Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction SilentlyContinue) {
    Bad 'port 11434 is still held. Reboot and re-run this script.'
    Get-NetTCPConnection -LocalPort 11434 -State Listen |
        Select-Object LocalAddress, LocalPort, OwningProcess | Format-Table | Out-Host
    exit 1
}
Ok 'port 11434 is free'

# A tray app that starts at logon will recreate this problem after every
# reboot, so say so rather than leaving a fix that quietly comes undone.
$autoRun = @()
foreach ($k in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
               'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run') {
    if (Test-Path $k) {
        (Get-Item $k).Property | Where-Object { $_ -match 'ollama' } |
            ForEach-Object { $autoRun += "$k\$_" }
    }
}
$startup = Get-ChildItem -Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup" `
             -Filter '*ollama*' -ErrorAction SilentlyContinue
if ($autoRun -or $startup) {
    Warn 'the Ollama desktop app is set to start automatically:'
    $autoRun | ForEach-Object { Write-Host "         $_" }
    $startup | ForEach-Object { Write-Host "         $($_.FullName)" }
    Warn 'remove those, or it will start a second server again at next logon.'
}

# ── 3. Find the model blobs rather than re-download 9 GB ───────────────────
Step 'Locating model blobs already on disk'

New-Item -ItemType Directory -Path $modelsPath -Force | Out-Null

function BlobBytes($dir) {
    $b = Join-Path $dir 'blobs'
    if (-not (Test-Path $b)) { return 0 }
    return [int64]((Get-ChildItem $b -File -ErrorAction SilentlyContinue |
                    Measure-Object -Property Length -Sum).Sum)
}

# Everywhere a server could have put them: our configured path, and the stock
# default (~/.ollama/models) for SYSTEM and for every real user profile.
$candidates = [System.Collections.Generic.List[string]]::new()
$candidates.Add($modelsPath)
$candidates.Add('C:\Windows\System32\config\systemprofile\.ollama\models')
Get-ChildItem 'C:\Users' -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    $candidates.Add((Join-Path $_.FullName '.ollama\models'))
}

$found = @()
foreach ($c in ($candidates | Select-Object -Unique)) {
    if (Test-Path $c) {
        $sz = BlobBytes $c
        Write-Host ("  {0,10:N0} MB  {1}" -f ($sz / 1MB), $c)
        if ($sz -gt 0) { $found += [pscustomobject]@{ Path = $c; Bytes = $sz } }
    }
}
if ($found.Count -eq 0) { Write-Host '  no blobs found anywhere' }

# Consolidate into the configured path so there is exactly one store. Moved,
# not copied: two 9 GB copies is a waste, and a stale store is what caused this.
$strays = $found | Where-Object { $_.Path -ne $modelsPath }
foreach ($s in $strays) {
    Write-Host "  moving $([math]::Round($s.Bytes/1MB,0)) MB from $($s.Path)"
    # robocopy handles cross-volume moves and long paths, which Move-Item does not.
    $null = robocopy $s.Path $modelsPath /E /MOVE /NFL /NDL /NJH /NJS /NP /R:1 /W:1
    if ($LASTEXITCODE -ge 8) {
        Warn "robocopy exit $LASTEXITCODE - some files did not move; the pull below will fill any gaps"
    } else {
        Ok "moved (robocopy exit $LASTEXITCODE)"
    }
}
Ok "model store: $modelsPath ($([math]::Round((BlobBytes $modelsPath)/1MB,0)) MB)"

# SYSTEM runs the server, so it has to be able to read and write the store.
$acl = Get-Acl $modelsPath
$acl.SetAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    'NT AUTHORITY\SYSTEM', 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
Set-Acl -Path $modelsPath -AclObject $acl
Ok 'SYSTEM has full control of the model store'

# ── 4. Start ours, and only ours ───────────────────────────────────────────
Step 'Starting the scheduled task'

if (-not (Get-ScheduledTask -TaskName 'Ollama' -ErrorAction SilentlyContinue)) {
    throw 'The "Ollama" scheduled task does not exist. Run install-ollama-gpu.ps1 first.'
}
Start-ScheduledTask -TaskName 'Ollama'

$up = $false
foreach ($i in 1..30) {
    Start-Sleep -Seconds 2
    try {
        Invoke-WebRequest -Uri 'http://127.0.0.1:11434/api/tags' -UseBasicParsing -TimeoutSec 5 | Out-Null
        $up = $true; break
    } catch { }
}
if (-not $up) {
    Bad 'the API did not come up. Check: Get-ScheduledTaskInfo -TaskName Ollama'
    exit 1
}
Ok 'API responding'

# ── 5. Verify the things that were silently wrong ──────────────────────────
Step 'Verifying'

$fail = 0

# 5a. Exactly one listener, and it must be a wildcard. This is the check whose
#     absence let the original fault through: everything else looked healthy.
$listeners = @(Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction SilentlyContinue)
$addrs = ($listeners | ForEach-Object { $_.LocalAddress }) -join ', '
if ($listeners.Count -ne 1) {
    Bad "$($listeners.Count) listeners on 11434 ($addrs) - expected exactly 1"
    $fail++
} elseif ($listeners[0].LocalAddress -notin '0.0.0.0', '::') {
    Bad "listening on $($listeners[0].LocalAddress) only - unreachable from other hosts. OLLAMA_HOST did not take effect."
    $fail++
} else {
    Ok "one listener, on the wildcard address ($addrs)"
}

# 5b. The server must actually list the model. A pull that reported success
#     proves nothing about which server received it.
try {
    $tags = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 30
    $names = @($tags.models | ForEach-Object { $_.name })
    if ($names.Count -eq 0) {
        Warn 'the server lists no models - pulling now'
        & $exe pull $Model
        $tags  = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 30
        $names = @($tags.models | ForEach-Object { $_.name })
    }
    if ($names -contains $Model) { Ok "model listed: $($names -join ', ')" }
    else {
        Warn "$Model not listed (found: $($names -join ', ')) - pulling"
        & $exe pull $Model
        Ok 'pulled'
    }
} catch {
    Bad "could not read /api/tags: $($_.Exception.Message)"
    $fail++
}

# 5c. Inference works, is on the GPU, and keep-alive took effect. A 5-minute
#     UNTIL means the environment block never reached the server process, which
#     would mean OLLAMA_MODELS and OLLAMA_HOST are wrong too.
Step 'Benchmark'

# Warm the model first. On a cold server the first request's timings include the
# model load - a 9.6 GB read showed up as "39 prompt tokens at 2.1 tok/s", which
# reads as a catastrophic fault rather than the disk I/O it actually was.
Write-Host '  loading the model (a cold load takes 15-35s and is not measured)...'
try {
    Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/generate' -Method Post -TimeoutSec 900 `
      -ContentType 'application/json' `
      -Body (@{ model = $Model; prompt = 'hi'; stream = $false
                options = @{ num_predict = 1 } } | ConvertTo-Json) | Out-Null
    Ok 'model resident'
} catch {
    Bad "could not load the model: $($_.Exception.Message)"
    $fail++
}

$body = @{ model = $Model; stream = $false
           prompt = 'Write one paragraph explaining what a DNS resolver does.'
           options = @{ num_predict = 200; temperature = 0.1 } } | ConvertTo-Json
try {
    $b = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/generate' -Method Post `
           -Body $body -ContentType 'application/json' -TimeoutSec 900
    Write-Host ("  output: {0} tokens at {1} tok/s" -f $b.eval_count,
        [math]::Round($b.eval_count / ($b.eval_duration / 1e9), 1)) -ForegroundColor Green
    Write-Host ("  prompt: {0} tokens at {1} tok/s" -f $b.prompt_eval_count,
        [math]::Round($b.prompt_eval_count / ($b.prompt_eval_duration / 1e9), 1)) -ForegroundColor Green
} catch {
    Bad "generate failed: $($_.Exception.Message)"
    $fail++
}

Write-Host '  processor split and keep-alive:'
& $exe ps
Warn 'If UNTIL above says about 5 minutes, OLLAMA_KEEP_ALIVE did not apply: the'
Warn 'task inherited a stale environment. Fix with: Stop-ScheduledTask -TaskName Ollama;'
Warn 'Start-ScheduledTask -TaskName Ollama   (or reboot).'

Step 'Done'
if ($fail -gt 0) {
    Bad "$fail check(s) failed - see above."
    exit 1
}
$ips = (Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' }
       ).IPAddress -join ', '
Write-Host @"
  All checks passed.

  This host: $ips
  Confirm from the report server before declaring victory - local success is
  exactly what masked the original fault:

      Invoke-RestMethod http://<this host>:11434/api/tags

  It must list $Model. If it returns an empty list, you are still reaching a
  different server than the local tests are.
"@ -ForegroundColor Cyan
