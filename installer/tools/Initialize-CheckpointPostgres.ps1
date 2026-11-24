# First-install PostgreSQL init for Checkpoint (port 5433, localhost only).
param(
    [string] $PgRoot = "C:\Program Files\Checkpoint\PostgreSQL",
    [string] $PgData = "C:\ProgramData\Checkpoint\pgdata",
    [int] $Port = 5433,
    [string] $SuperPassword
)

$ErrorActionPreference = "Stop"
$initdb = Join-Path $PgRoot "bin\initdb.exe"
$pgCtl = Join-Path $PgRoot "bin\pg_ctl.exe"

if (-not (Test-Path -LiteralPath $initdb)) {
    throw "initdb missing at $initdb"
}

if (-not (Test-Path -LiteralPath $PgData)) {
    New-Item -ItemType Directory -Force -Path $PgData | Out-Null
    $pwFile = Join-Path $env:TEMP "checkpoint-initdb-pw.txt"
    Set-Content -LiteralPath $pwFile -Value $SuperPassword -NoNewline -Encoding ASCII
  & $initdb -U postgres -A scram-sha-256 -E UTF8 -D $PgData --pwfile=$pwFile
    Remove-Item -LiteralPath $pwFile -Force
    if ($LASTEXITCODE -ne 0) {
        throw "initdb failed"
    }
}

$conf = Join-Path $PgData "postgresql.conf"
if (Test-Path -LiteralPath $conf) {
    $text = Get-Content -LiteralPath $conf -Raw
    $text = $text -replace "port\s*=\s*\d+", "port = $Port"
    if ($text -notmatch "listen_addresses") {
        $text += "`nlisten_addresses = '127.0.0.1'`n"
    } else {
        $text = $text -replace "listen_addresses\s*=\s*'[^']*'", "listen_addresses = '127.0.0.1'"
    }
    Set-Content -LiteralPath $conf -Value $text -NoNewline
}

$svc = Get-Service CheckpointPostgres -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne "Running") {
        Start-Service CheckpointPostgres -ErrorAction SilentlyContinue
        $svc.Refresh()
    }
    if ($svc.Status -ne "Running") {
        & $pgCtl stop -D $PgData -m fast 2>&1 | Out-Null
        & sc.exe delete CheckpointPostgres 2>&1 | Out-Null
        $svc = $null
    }
}

if (-not $svc) {
    & $pgCtl register -N CheckpointPostgres -D $PgData -S auto
    if ($LASTEXITCODE -ne 0) {
        throw "pg_ctl register CheckpointPostgres failed exit $LASTEXITCODE"
    }
}

Start-Service CheckpointPostgres -ErrorAction Stop
if ((Get-Service CheckpointPostgres).Status -ne 'Running') {
    throw "CheckpointPostgres failed to reach Running after Start-Service"
}
Start-Sleep -Seconds 3

$psql = Join-Path $PgRoot "bin\psql.exe"
$exists = $false
$env:PGPASSWORD = $SuperPassword
& $psql -h 127.0.0.1 -p $Port -U postgres -tc "SELECT 1 FROM pg_database WHERE datname='checkpoint'" | ForEach-Object {
    if ($_ -match "1") { $exists = $true }
}
if (-not $exists) {
    & $psql -h 127.0.0.1 -p $Port -U postgres -c "CREATE DATABASE checkpoint;"
    if ($LASTEXITCODE -ne 0) {
        throw "CREATE DATABASE checkpoint failed"
    }
}