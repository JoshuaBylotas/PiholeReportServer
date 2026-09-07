<#
.SYNOPSIS
    Creates the PiholeReportServer event log and its sources.

.DESCRIPTION
    Registering an event source needs administrator, and neither the IIS app pool
    nor a first run of the app has that. So it is done here, once, deliberately.

    If the source is missing the EventLog provider silently drops messages rather
    than failing to start. That is the right behaviour for a logging sink and a
    poor way to find out it was never set up, which is why this script exists and
    why the deploy calls it.

    Sources:
      ReportServer      the ASP.NET Core site
      PiholeCollectors  the device-name collectors (created by Register-Collectors)

.EXAMPLE
    .\Register-EventLog.ps1
    .\Register-EventLog.ps1 -ComputerName WINSERVER03 -Source ReportServer
#>
[CmdletBinding()]
param(
    [string]$ComputerName = 'WINSERVER03',
    [string]$LogName      = 'PiholeReportServer',
    [string[]]$Source     = @('ReportServer')
)

$ErrorActionPreference = 'Stop'

$result = Invoke-Command -ComputerName $ComputerName -ArgumentList $LogName, $Source -ScriptBlock {
    param($log, $sources)
    foreach ($s in $sources) {
        try {
            if ([System.Diagnostics.EventLog]::SourceExists($s)) {
                # A source can only belong to one log. If it is registered against
                # the wrong one, writes go somewhere unexpected rather than failing,
                # so say which log it is actually on.
                $existing = [System.Diagnostics.EventLog]::LogNameFromSourceName($s, '.')
                if ($existing -eq $log) {
                    "OK   '$s' already registered against '$log'"
                } else {
                    "WARN '$s' is registered against '$existing', not '$log' - events will go there"
                }
            } else {
                New-EventLog -LogName $log -Source $s -ErrorAction Stop
                "OK   created '$log' with source '$s'"
            }
        } catch {
            "FAIL '$s': $($_.Exception.Message)"
        }
    }
}

$result | ForEach-Object { "  $_" }
if ($result -match '^FAIL') { exit 1 }
exit 0
