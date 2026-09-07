<#
.SYNOPSIS
    Sets up Ollama as a GPU-backed inference host for the Pi-hole Report Server.

.DESCRIPTION
    Run this ON the machine with the NVIDIA GPU, in an ELEVATED PowerShell window.

    Sized for a 16 GB card: qwen2.5:14b-instruct at Q4_K_M is about 9 GB, which
    leaves comfortable room for a large context window. A 7B would also run, far
    faster, but the 14B is the point of having the card - the 7B currently in use
    ignored an explicit prompt rule and produced a JOIN that inflates per-device
    counts, which a 14B is much less prone to.

    Installs Ollama as a Windows service via a scheduled task rather than the
    desktop installer, because this has to run headless with no user logged on.

.PARAMETER AllowFrom
    IP addresses permitted to reach the inference port. Defaults to the report
    server's two addresses. Pass @() to skip firewall rules entirely.

.PARAMETER Model
    Model to pull. See the table in the notes for 16 GB alternatives.

.EXAMPLE
    .\install-ollama-gpu.ps1
    .\install-ollama-gpu.ps1 -Model 'qwen2.5-coder:14b'

.NOTES
    Model choices for a 16 GB card:
      qwen2.5:14b-instruct   ~9 GB   recommended: good reasoning AND decent SQL
      qwen2.5-coder:14b      ~9 GB   better SQL, weaker at prose conclusions
      qwen2.5:32b-instruct  ~20 GB   DOES NOT FIT - will spill to CPU and crawl
    Only one is loaded at a time (OLLAMA_MAX_LOADED_MODELS=1); two 9 GB models
    exceed 16 GB.
#>
[CmdletBinding()]
param(
    [string[]]$AllowFrom = @('10.20.0.15', '10.20.0.16'),
    [string]$Model = 'qwen2.5:14b-instruct',
    [string]$InstallDir = 'C:\Ollama'
)

$ErrorActionPreference = 'Stop'

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "  OK   $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "  WARN $msg" -ForegroundColor Yellow }

# ── 0. Preconditions ────────────────────────────────────────────────────────
Step 'Checking prerequisites'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this in an elevated PowerShell window (Run as Administrator).'
}
Ok 'running elevated'

# The whole point of this host is the GPU, so fail loudly rather than silently
# falling back to CPU and being slower than what we already have.
$smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if (-not $smi) {
    throw 'nvidia-smi not found. Install the NVIDIA driver first (GeForce/Studio driver is enough; CUDA toolkit is NOT required - Ollama bundles what it needs).'
}
$gpu = & nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader
Ok "GPU: $gpu"

$vramMb = [int](( & nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits) -split "`n")[0].Trim()
if ($vramMb -lt 15000) {
    Warn "Only ${vramMb} MiB of VRAM detected. A 14B Q4 needs ~9 GB plus context; consider -Model 'qwen2.5:7b-instruct'."
}

# ── 1. Download and extract ─────────────────────────────────────────────────
Step 'Installing Ollama'

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
$zip = Join-Path $InstallDir 'ollama-windows-amd64.zip'

Write-Host '  downloading (about 1.4 GB, it bundles the CUDA runtime)...'
Invoke-WebRequest -Uri 'https://ollama.com/download/ollama-windows-amd64.zip' `
                  -OutFile $zip -UseBasicParsing -TimeoutSec 3600
Ok "downloaded $([math]::Round((Get-Item $zip).Length/1MB,0)) MB"

Expand-Archive -Path $zip -DestinationPath $InstallDir -Force
if (-not (Test-Path (Join-Path $InstallDir 'ollama.exe'))) {
    throw "ollama.exe not found in $InstallDir after extraction."
}
Ok "extracted to $InstallDir"

# ── 2. Configuration ────────────────────────────────────────────────────────
Step 'Configuring'

$vars = [ordered]@{
    # Must listen off-loopback for the report server to reach it.
    'OLLAMA_HOST'              = '0.0.0.0:11434'
    'OLLAMA_MODELS'            = (Join-Path $InstallDir 'models')
    # A cold load costs seconds even on GPU; keep the model resident.
    'OLLAMA_KEEP_ALIVE'        = '60m'
    # Two 9 GB models will not fit in 16 GB.
    'OLLAMA_MAX_LOADED_MODELS' = '1'
    # 2 slots so a long batch job cannot block an interactive question. This was a
    # real problem at 1: a classification backfill blocked a question for 148s.
    'OLLAMA_NUM_PARALLEL'      = '2'
    # Meaningful speed-up on Ampere and later, and it reduces KV cache size.
    'OLLAMA_FLASH_ATTENTION'   = '1'
    # Quantised KV cache: roughly halves context memory for no practical quality
    # loss, which is what lets a 14B keep a big context on a 16 GB card.
    'OLLAMA_KV_CACHE_TYPE'     = 'q8_0'
}
foreach ($k in $vars.Keys) {
    [Environment]::SetEnvironmentVariable($k, $vars[$k], 'Machine')
    Set-Item -Path "env:$k" -Value $vars[$k]
}
New-Item -ItemType Directory -Path $vars['OLLAMA_MODELS'] -Force | Out-Null
Ok ($vars.Keys -join ', ')

# ── 3. Run headless ────────────────────────────────────────────────────────
Step 'Registering the service'

# A pre-existing server has to go first. Windows allows two listeners on one
# port when one binds IPv6 wildcard and the other binds IPv4 loopback, so ours
# would start without error, the model would pull into whichever store the
# loopback server uses, and remote clients would hit the empty one. That failure
# is invisible locally - every local test reaches the loopback server and passes.
$existing = @(Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) {
    Warn "port 11434 is already in use by: $(($existing | ForEach-Object { "$($_.LocalAddress) (pid $($_.OwningProcess))" }) -join ', ')"
    # 'ollama app' is the desktop tray build, and it respawns 'ollama serve'.
    Get-Process -Name 'ollama app', 'ollama_llama_server', 'ollama' -ErrorAction SilentlyContinue |
        ForEach-Object {
            try { Stop-Process -Id $_.Id -Force -ErrorAction Stop; Ok "stopped pre-existing pid $($_.Id) ($($_.ProcessName))" }
            catch { Warn "could not stop pid $($_.Id): $($_.Exception.Message)" }
        }
    foreach ($i in 1..15) {
        Start-Sleep -Milliseconds 500
        if (-not (Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction SilentlyContinue)) { break }
    }
    if (Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction SilentlyContinue) {
        throw 'Port 11434 is still held by another process. Reboot, then re-run this script. If the Ollama desktop app is installed, uninstall it or remove it from startup - it will keep taking the port.'
    }
    Ok 'port 11434 released'
}

# A scheduled task, not sc.exe: ollama.exe has no service-control interface, and
# Task Scheduler gives a priority setting natively.
$action    = New-ScheduledTaskAction -Execute (Join-Path $InstallDir 'ollama.exe') -Argument 'serve'
$trigger   = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                -ExecutionTimeLimit ([TimeSpan]::Zero) `
                -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName 'Ollama' -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings `
    -Description 'GPU inference for the Pi-hole Report Server' -Force | Out-Null

Start-ScheduledTask -TaskName 'Ollama'
Ok 'task registered and started'

Write-Host '  waiting for the API...'
$up = $false
foreach ($i in 1..30) {
    Start-Sleep -Seconds 2
    try {
        Invoke-WebRequest -Uri 'http://127.0.0.1:11434/api/tags' -UseBasicParsing -TimeoutSec 5 | Out-Null
        $up = $true; break
    } catch { }
}
if (-not $up) { throw 'Ollama did not start. Check: Get-ScheduledTaskInfo -TaskName Ollama' }
Ok 'API responding on 11434'

# "API responding" only proves SOMETHING answered on loopback. What matters is
# that it is ours and that it is reachable from off-box, which means exactly one
# listener, on a wildcard address.
$lsn = @(Get-NetTCPConnection -LocalPort 11434 -State Listen -ErrorAction SilentlyContinue)
$addrs = ($lsn | ForEach-Object { $_.LocalAddress }) -join ', '
if ($lsn.Count -gt 1) {
    throw "Two servers are listening on 11434 ($addrs). Local tests would pass and remote ones would fail. Run repair-ollama-host.ps1."
}
if ($lsn.Count -eq 1 -and $lsn[0].LocalAddress -notin '0.0.0.0', '::') {
    throw "Ollama is listening on $($lsn[0].LocalAddress) only, so no other host can reach it. OLLAMA_HOST did not take effect; reboot and re-run."
}
Ok "single listener on the wildcard address ($addrs)"

# ── 4. Firewall ────────────────────────────────────────────────────────────
Step 'Firewall'

if ($AllowFrom.Count -eq 0) {
    Warn 'skipped by request - the port is open to anything that can route to this host'
} else {
    # Ollama has NO authentication of its own. Anyone who can reach this port can
    # use the model and enumerate what is installed.
    Get-NetFirewallRule -DisplayName 'Ollama*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

    # An Allow rule ONLY. Windows Firewall evaluates Block rules BEFORE Allow
    # rules, so adding a companion "block everything else" rule on the same port
    # denies the permitted addresses too - it wins over this Allow and closes the
    # port completely. The default inbound action is already Block, so anything
    # not matching this rule is refused without help.
    New-NetFirewallRule -DisplayName 'Ollama (report server only)' -Direction Inbound `
        -Protocol TCP -LocalPort 11434 -Action Allow -RemoteAddress $AllowFrom -Profile Any | Out-Null
    Ok "allowed from $($AllowFrom -join ', '); everything else falls to the default deny"

    # Rules are inert if the active profile's firewall is off - which is exactly
    # what happened on WINSERVER01 and left the endpoint open without me noticing.
    $off = Get-NetFirewallProfile -PolicyStore ActiveStore | Where-Object { -not $_.Enabled }
    if ($off) {
        Warn "Windows Firewall is DISABLED for: $($off.Name -join ', '). The rules above will NOT be enforced."
        Warn 'Enable those profiles, or accept that the port is reachable from the LAN.'
    }
    # Verify rather than assume. A firewall rule that looks correct and denies
    # everything anyway is exactly the failure this is guarding against.
    $probe = Test-NetConnection -ComputerName $env:COMPUTERNAME -Port 11434 -WarningAction SilentlyContinue
    if (-not $probe.TcpTestSucceeded) {
        Warn 'Port 11434 is not reachable even locally by name - check the rules before continuing.'
    } else {
        Ok 'port 11434 reachable'
    }
}

# ── 5. Model ───────────────────────────────────────────────────────────────
Step "Pulling $Model"
& (Join-Path $InstallDir 'ollama.exe') pull $Model

# A pull that prints "success" says nothing about WHICH server stored it, so ask
# the server we started what it can actually serve. This is the check that would
# have caught the two-listener fault immediately instead of at the remote client.
$tags = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/tags' -TimeoutSec 60
$names = @($tags.models | ForEach-Object { $_.name })
if ($names -notcontains $Model) {
    throw "The pull reported success but the running server does not list $Model (it lists: $(if ($names) { $names -join ', ' } else { 'nothing' })). The model went to a different server's store. Run repair-ollama-host.ps1."
}
Ok "model pulled and served by this instance ($($names -join ', '))"

# ── 6. Verify it is actually on the GPU ────────────────────────────────────
Step 'Verifying GPU offload'

$body = @{ model = $Model; prompt = 'Reply with the single word: ready'; stream = $false
           options = @{ num_predict = 8 } } | ConvertTo-Json
$sw = [Diagnostics.Stopwatch]::StartNew()
$r  = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/generate' -Method Post `
        -Body $body -ContentType 'application/json' -TimeoutSec 600
$sw.Stop()
Ok "first response in $([math]::Round($sw.Elapsed.TotalSeconds,1))s (includes model load)"

# "100% GPU" here is the thing to confirm. Any CPU share means part of the model
# spilled to system RAM and throughput will be a fraction of what it should be.
Write-Host '  processor split:' -NoNewline
& (Join-Path $InstallDir 'ollama.exe') ps

Step 'Benchmark'
$bench = @{ model = $Model; stream = $false
            prompt = 'Write one paragraph explaining what a DNS resolver does.'
            options = @{ num_predict = 200; temperature = 0.1 } } | ConvertTo-Json
$b = Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/generate' -Method Post `
       -Body $bench -ContentType 'application/json' -TimeoutSec 600
$tps = [math]::Round($b.eval_count / ($b.eval_duration / 1e9), 1)
Write-Host ("  output: {0} tokens at {1} tok/s" -f $b.eval_count, $tps) -ForegroundColor Green
Write-Host ("  prompt: {0} tokens at {1} tok/s" -f $b.prompt_eval_count,
            [math]::Round($b.prompt_eval_count / ($b.prompt_eval_duration / 1e9), 1)) -ForegroundColor Green

Step 'Done'
Write-Host @"
  Report back:  the tok/s figures above, and this machine's IP.

  For reference, the current CPU host manages 9.8 tok/s on a 7B. Anything above
  about 30 tok/s on this 14B makes the analysis agent genuinely usable - a
  four-step investigation drops from roughly ten minutes to under one.

  I then repoint the report server with two settings, no redeploy:
      Ai__Endpoint = http://<this machine's IP>:11434
      Ai__Model    = $Model
"@ -ForegroundColor Cyan
