# Probe pgdata for reinstall-kept (a) vs refuse (b). Writes one word: ABSENT, VALID, REFUSE.
param(
    [string] $PgData = "C:\ProgramData\Checkpoint\pgdata",
    [int] $ExpectedMajor = 16,
    [string] $PgRoot = "C:\Program Files\Checkpoint\PostgreSQL",
    [string] $ResultFile = ""
)

$ErrorActionPreference = "Stop"

function Write-ProbeResult([string] $Word) {
    if ($ResultFile) {
        [System.IO.File]::WriteAllText($ResultFile, $Word, [System.Text.UTF8Encoding]::new($false))
    }
    Write-Output $Word
}

if (-not (Test-Path -LiteralPath $PgData)) {
    Write-ProbeResult "ABSENT"
    exit 0
}

$pgVerFile = Join-Path $PgData "PG_VERSION"
if (-not (Test-Path -LiteralPath $pgVerFile)) {
    Write-ProbeResult "REFUSE"
    exit 0
}

$major = (Get-Content -LiteralPath $pgVerFile -Raw).Trim()
if ($major -ne [string]$ExpectedMajor) {
    Write-ProbeResult "REFUSE"
    exit 0
}

$pgControldata = Join-Path $PgRoot "bin\pg_controldata.exe"
if (-not (Test-Path -LiteralPath $pgControldata)) {
    Write-ProbeResult "VALID"
    exit 0
}

$out = & $pgControldata -D $PgData 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-ProbeResult "REFUSE"
    exit 0
}
if ($out -match '(?i)could not|invalid|corrupt|error') {
    Write-ProbeResult "REFUSE"
    exit 0
}

Write-ProbeResult "VALID"
exit 0