<#
    Shared logging for the collectors: everything goes to the Windows event log.

    A scheduled task's console output goes nowhere, and a transcript file on a
    domain controller is somewhere nobody looks. The event log is where this
    machine's other failures already surface, so it is where these belong: one
    event per run, carrying the whole run, under a log of its own in
    Applications and Services Logs.

    Event ids are stable so they can be filtered and alerted on:

      1000  run completed cleanly
      2000  run completed, but something was skipped or looked wrong
      3000  run failed

    Dot-source this, then use Step/Ok/Warn in place of Write-Host. Each call
    writes to the console (useful when running by hand) and appends to a buffer
    that Write-RunEvent emits as a single event at the end. One event per run
    rather than per line: a run is the unit you care about, and forty events at
    00:30 every night is noise that trains you to ignore the log.
#>

$script:EventLogName   = 'PiholeReportServer'
$script:EventSource    = 'PiholeCollectors'
$script:LogBuffer      = [System.Text.StringBuilder]::new()
$script:SawWarning     = $false
$script:EventLogUsable = $null

function Initialize-EventLogging {
    <#
        Creates the log and source if they are missing. Needs administrator, which
        the scheduled task has as SYSTEM; when run by hand without it, this fails
        soft and the run still works, just without an event.
    #>
    if ($null -ne $script:EventLogUsable) { return $script:EventLogUsable }

    try {
        if (-not [System.Diagnostics.EventLog]::SourceExists($script:EventSource)) {
            New-EventLog -LogName $script:EventLogName -Source $script:EventSource -ErrorAction Stop
        }
        $script:EventLogUsable = $true
    } catch {
        Write-Host "  WARN could not register the event source: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "       (run once as administrator to create it; the collection still ran)" -ForegroundColor Yellow
        $script:EventLogUsable = $false
    }
    return $script:EventLogUsable
}

function Add-LogLine([string]$Text) {
    [void]$script:LogBuffer.AppendLine($Text)
}

function Step([string]$m) {
    Write-Host "`n=== $m ===" -ForegroundColor Cyan
    Add-LogLine ''
    Add-LogLine "=== $m ==="
}

function Ok([string]$m) {
    Write-Host "  OK   $m" -ForegroundColor Green
    Add-LogLine "  OK   $m"
}

function Warn([string]$m) {
    Write-Host "  WARN $m" -ForegroundColor Yellow
    Add-LogLine "  WARN $m"
    $script:SawWarning = $true
}

function Detail([string]$m) {
    Write-Host "       $m"
    Add-LogLine "       $m"
}

function Write-RunEvent {
    <#
        Emits the buffered run as one event.

        .PARAMETER Failure
            The terminating error, when the run did not finish.
    #>
    param([string]$Failure)

    if (-not (Initialize-EventLogging)) { return }

    $body = $script:LogBuffer.ToString()
    if ($Failure) {
        $body = "The collection FAILED.`n`n$Failure`n`n--- run so far ---`n$body"
        $entryType = 'Error'
        $eventId   = 3000
    } elseif ($script:SawWarning) {
        $entryType = 'Warning'
        $eventId   = 2000
    } else {
        $entryType = 'Information'
        $eventId   = 1000
    }

    $header = "{0} on {1}`n" -f
        [IO.Path]::GetFileNameWithoutExtension($script:RunScriptName), $env:COMPUTERNAME
    $message = $header + $body

    # An event message is capped at 32 KB. Keep the tail: the failure and the
    # summary are at the end, which is the part worth having.
    if ($message.Length -gt 30000) {
        $message = "[earlier output trimmed]`n" + $message.Substring($message.Length - 30000)
    }

    try {
        Write-EventLog -LogName $script:EventLogName -Source $script:EventSource `
            -EventId $eventId -EntryType $entryType -Message $message -ErrorAction Stop
    } catch {
        Write-Host "  WARN could not write the event: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
