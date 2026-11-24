# Probe pgdata for reinstall-kept (a) vs refuse (b). Writes one word to stdout: ABSENT, VALID, REFUSE.
param(
    [string] $PgData = "C:\ProgramData\Checkpoint\pgdata",
    [int] $ExpectedMajor = 16,
    [string] $PgRoot = "C:\Program Files\Checkpoint\PostgreSQL"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $PgData)) {
    Write-Output "ABSENT"
    exit 0
}

$pgVerFile = Join-Path $PgData "PG_VERSION"
if (-not (Test-Path -LiteralPath $pgVerFile)) {
    Write-Output "REFUSE"
    exit 0
}

$major = (Get-Content -LiteralPath $pgVerFile -Raw).Trim()
if ($major -ne [string]$ExpectedMajor) {
    Write-Output "REFUSE"
    exit 0
}

$pgControldata = Join-Path $PgRoot "bin\pg_controldata.exe"
if (-not (Test-Path -LiteralPath $pgControldata)) {
    Write-Output "VALID"
    exit 0
}

$out = & $pgControldata -D $PgData 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Output "REFUSE"
    exit 0
}
if ($out -match '(?i)could not|invalid|corrupt|error') {
    Write-Output "REFUSE"
    exit 0
}

Write-Output "VALID"
exit 0