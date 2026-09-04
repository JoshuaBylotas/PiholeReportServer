<#
.SYNOPSIS
    Validates every report SQL file against a live database without executing it.

.DESCRIPTION
    sys.sp_describe_first_result_set parses and binds the statement — so it catches
    invalid column names, bad joins and type errors — but never runs it. That makes it
    safe and fast against a table with tens of millions of rows.

    This exists because `dotnet build` and the unit tests cannot see the database
    schema. Report SQL is deployed as content, so a wrong column name compiles, ships,
    passes CI, and only fails when somebody opens that report. Two such bugs
    (DimClient.hostname, DimClient.vendor) reached production before this script did.

    Not part of CI, which has no database. Run it after changing any report SQL, and
    after any change to the loader's schema.

.PARAMETER Server
    SQL Server host. Defaults to the Sql__Server environment variable.

.PARAMETER Database
    Database name. Defaults to Sql__Database, then 'pihole'.

.EXAMPLE
    .\tools\Validate-ReportSql.ps1 -Server WINSERVER01
#>
[CmdletBinding()]
param(
    [string]$Server   = $(if ($env:Sql__Server)   { $env:Sql__Server }   else { '' }),
    [string]$Database = $(if ($env:Sql__Database) { $env:Sql__Database } else { 'pihole' })
)

$ErrorActionPreference = 'Stop'

if (-not $Server) {
    throw "Specify -Server, or set the Sql__Server environment variable."
}

$repoRoot   = Split-Path -Parent $PSScriptRoot
$reportsDir = Join-Path $repoRoot 'src\PiholeReportServer\Reports'
$manifest   = Join-Path $reportsDir 'reports.json'

if (-not (Test-Path $manifest)) { throw "Manifest not found: $manifest" }

# Parameter kind -> a T-SQL type good enough for binding.
$typeMap = @{
    'Date'     = 'date'
    'DateTime' = 'datetime2'
    'Int'      = 'int'
    'Text'     = 'varchar(255)'
    'Choice'   = 'varchar(255)'
}

$reports = (Get-Content $manifest -Raw | ConvertFrom-Json).reports
$results = [System.Collections.Generic.List[object]]::new()

function Test-Sql {
    param([string]$Name, [string]$Sql, [string]$ParamDecl)

    # Single-quote escaping for the nested N'...' literals.
    $sqlLit   = $Sql       -replace "'", "''"
    $paramLit = $ParamDecl -replace "'", "''"

    $probe = if ($ParamDecl) {
        "EXEC sys.sp_describe_first_result_set @tsql = N'$sqlLit', @params = N'$paramLit';"
    } else {
        "EXEC sys.sp_describe_first_result_set @tsql = N'$sqlLit';"
    }

    $tmp = New-TemporaryFile
    try {
        Set-Content -Path $tmp -Value $probe -Encoding UTF8
        $out = & sqlcmd -S $Server -E -C -d $Database -b -h -1 -W -i $tmp 2>&1
        if ($LASTEXITCODE -eq 0) {
            [pscustomobject]@{ Name = $Name; Ok = $true;  Detail = '' }
        } else {
            $msg = ($out | Where-Object { $_ -match '\S' } | Select-Object -First 3) -join ' | '
            [pscustomobject]@{ Name = $Name; Ok = $false; Detail = $msg }
        }
    } finally {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "Validating report SQL against $Server/$Database" -ForegroundColor Cyan
Write-Host ("-" * 78)

foreach ($r in $reports) {
    $file = Join-Path $reportsDir "$($r.id).sql"
    if (-not (Test-Path $file)) {
        $results.Add([pscustomobject]@{ Name = $r.id; Ok = $false; Detail = 'SQL FILE MISSING' })
        continue
    }

    $decl = @()
    foreach ($p in $r.parameters) {
        $t = $typeMap[[string]$p.kind]
        if (-not $t) { $t = 'varchar(255)' }
        $decl += "@$($p.name) $t"
    }

    $results.Add((Test-Sql -Name $r.id -Sql (Get-Content $file -Raw) -ParamDecl ($decl -join ', ')))
}

# Any .sql file not referenced by the manifest is dead weight or a missing entry.
Get-ChildItem $reportsDir -Filter '*.sql' | ForEach-Object {
    $id = [IO.Path]::GetFileNameWithoutExtension($_.Name)
    if (-not ($reports.id -contains $id)) {
        $results.Add([pscustomobject]@{ Name = $id; Ok = $false; Detail = 'ORPHAN: not in reports.json' })
    }
}

# Builder-generated SQL, if the generator dumped it (see Validate-BuilderSql).
$builderDir = Join-Path $env:TEMP 'builder-sql'
if (Test-Path $builderDir) {
    Get-ChildItem $builderDir -Filter '*.sql' | ForEach-Object {
        $results.Add((Test-Sql -Name "builder/$($_.BaseName)" `
            -Sql (Get-Content $_.FullName -Raw) `
            -ParamDecl '@limit int, @from datetime2, @to datetime2, @clientFilter varchar(255), @domainFilter varchar(255), @status int, @type int, @cli0 varchar(255), @cli1 varchar(255), @al0 int, @al1 int'))
    }
}

foreach ($r in $results) {
    if ($r.Ok) {
        Write-Host ("  PASS  {0}" -f $r.Name) -ForegroundColor Green
    } else {
        Write-Host ("  FAIL  {0}" -f $r.Name) -ForegroundColor Red
        Write-Host ("        {0}" -f $r.Detail) -ForegroundColor DarkYellow
    }
}

$failed = @($results | Where-Object { -not $_.Ok })
Write-Host ("-" * 78)
Write-Host ("{0} checked, {1} passed, {2} failed" -f $results.Count, ($results.Count - $failed.Count), $failed.Count)

if ($failed.Count -gt 0) { exit 1 }
exit 0
