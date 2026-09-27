<#
.SYNOPSIS
    Lists the DHCP address reservations to enter in the Omada controller.

.DESCRIPTION
    The Omada OpenAPI can write a client's name but has no route for its address
    reservation - ipSetting.useFixedAddr is readable and not writable, on any
    path. Verified by asking every candidate route what methods it accepts:

        PATCH /clients/{mac}/name        exists  (the push agent uses it)
        PATCH /clients/{mac}/ratelimit   exists
        POST  /clients/{mac}/block       exists
        ...  /clients/{mac}/ip-setting   404, as are ~20 other spellings

    So reservations are a manual job, and this produces the list to work from
    rather than leaving you to cross-reference two systems by hand.

    Why reserve at all: the push agent keeps the A record pointing at wherever a
    device currently is, so DNS stays CORRECT without reservations. A reservation
    makes the address STABLE, which means the record stops changing and anything
    that caches it stays right too.

    Only devices with a name you stated are listed. An address is not worth
    pinning for a device nobody has named, and publish_dns = 0 devices are
    deliberately excluded - a guest phone with a randomised MAC would only churn.

.PARAMETER SqlServer
    SQL Server holding the warehouse.

.PARAMETER CsvPath
    Also write the list as CSV. Defaults to the OneDrive project folder.

.PARAMETER IncludeUnstated
    Include devices whose name came from a source rather than from you. Off by
    default; those names can still change under you.

.EXAMPLE
    .\Get-OmadaReservations.ps1
#>
[CmdletBinding()]
param(
    [string]$SqlServer = 'WINSERVER01',
    [string]$Database  = 'pihole',
    [string]$CsvPath,
    [switch]$IncludeUnstated
)

$ErrorActionPreference = 'Stop'

$sql = @"
SELECT mac, device_name, current_ip, name_source, vendor, device_type
FROM dbo.vDeviceTruth
WHERE publish_dns = 1
  AND device_name IS NOT NULL
  AND current_ip  IS NOT NULL
  AND ($(if ($IncludeUnstated) { '1 = 1' } else { "name_source = 'manual'" }))
ORDER BY
    -- Grouped by subnet then address, because that is the order the controller
    -- lists clients in and makes a long session of typing less error-prone.
    CAST(PARSENAME(current_ip, 2) AS int),
    CAST(PARSENAME(current_ip, 1) AS int);
"@

$conn = New-Object System.Data.SqlClient.SqlConnection(
    "Server=$SqlServer;Database=$Database;Integrated Security=true;TrustServerCertificate=true")
$conn.Open()
try {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    $table = New-Object System.Data.DataTable
    $table.Load($cmd.ExecuteReader())
}
finally {
    $conn.Close()
}

if ($table.Rows.Count -eq 0) {
    Write-Host 'Nothing to reserve yet: no device has a name you have stated.' -ForegroundColor Yellow
    Write-Host 'Name some devices at https://status.bylotas.com/Devices first.'
    return
}

$rows = $table | ForEach-Object {
    [pscustomobject]@{
        # Omada displays and expects a MAC uppercase with dashes.
        MAC        = $_.mac.ToUpperInvariant().Replace(':', '-')
        ReserveIP  = $_.current_ip
        Name       = $_.device_name
        NameSource = $_.name_source
        Vendor     = if ($_.vendor -is [DBNull]) { '' } else { $_.vendor }
        Model      = if ($_.device_type -is [DBNull]) { '' } else { $_.device_type }
    }
}

Write-Host ''
Write-Host "Omada -> Clients -> pick the client -> Config -> IP Setting -> Fixed IP" -ForegroundColor Cyan
Write-Host "Network 'Default', then the address below. $($rows.Count) device(s)." -ForegroundColor Cyan
Write-Host ''
$rows | Format-Table -AutoSize MAC, ReserveIP, Name, Vendor, Model

if (-not $CsvPath) {
    $oneDrive = Join-Path $env:USERPROFILE 'OneDrive\Projects\Device Names'
    if (Test-Path (Split-Path $oneDrive -Parent)) {
        $null = New-Item -ItemType Directory -Force -Path $oneDrive
        $CsvPath = Join-Path $oneDrive "omada-reservations-$(Get-Date -Format yyyyMMdd).csv"
    }
}
if ($CsvPath) {
    $rows | Export-Csv -LiteralPath $CsvPath -NoTypeInformation -Encoding UTF8
    Write-Host "CSV: $CsvPath" -ForegroundColor Green
}
