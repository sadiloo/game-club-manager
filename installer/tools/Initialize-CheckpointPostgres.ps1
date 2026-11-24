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
$psql = Join-Path $PgRoot "bin\psql.exe"
$initLog = "C:\ProgramData\Checkpoint\logs\postgres-init.log"

function Write-InitLog([string] $Message) {
    $logDir = Split-Path $initLog -Parent
    if (-not (Test-Path -LiteralPath $logDir)) {
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    }
    Add-Content -LiteralPath $initLog -Value ("{0} {1}" -f (Get-Date -Format "o"), $Message)
}

function Test-PostgresSuperuserLogin([string] $Password) {
    $env:PGPASSWORD = $Password
    & $psql -h 127.0.0.1 -p $Port -U postgres -tc "SELECT 1" 2>$null | Out-Null
    return ($LASTEXITCODE -eq 0)
}

function Set-PostgresClusterConf {
    $conf = Join-Path $PgData "postgresql.conf"
    if (-not (Test-Path -LiteralPath $conf)) { return }
    $text = Get-Content -LiteralPath $conf -Raw
    $text = $text -replace "port\s*=\s*\d+", "port = $Port"
    if ($text -notmatch "listen_addresses") {
        $text += "`nlisten_addresses = '127.0.0.1'`n"
    } else {
        $text = $text -replace "listen_addresses\s*=\s*'[^']*'", "listen_addresses = '127.0.0.1'"
    }
    Set-Content -LiteralPath $conf -Value $text -NoNewline
}

function Repair-PostgresSuperuserPassword([string] $NewPassword) {
    $hbaPath = Join-Path $PgData "pg_hba.conf"
    if (-not (Test-Path -LiteralPath $hbaPath)) {
        throw "pg_hba.conf missing; cannot repair postgres password"
    }
    Write-InitLog "Repair-PostgresSuperuserPassword: begin"
    $hbaBackup = "$hbaPath.checkpoint-repair"
    Copy-Item -LiteralPath $hbaPath -Destination $hbaBackup -Force
    $trustLines = @()
    foreach ($line in (Get-Content -LiteralPath $hbaPath)) {
        $trim = $line.Trim()
        if ($trim.Length -eq 0 -or $trim.StartsWith("#")) {
            $trustLines += $line
            continue
        }
        $parts = $trim -split '\s+'
        if ($parts[0] -eq 'local' -and $parts.Count -ge 4) {
            $trustLines += ("{0} {1} {2} trust" -f $parts[0], $parts[1], $parts[2])
        } elseif ($parts[0] -eq 'host' -and $parts.Count -ge 5) {
            $trustLines += ("{0} {1} {2} {3} trust" -f $parts[0], $parts[1], $parts[2], $parts[3])
        } else {
            $trustLines += $line
        }
    }
    Set-Content -LiteralPath $hbaPath -Value $trustLines -Encoding ASCII
    Restart-Service CheckpointPostgres -Force -ErrorAction Stop
    Start-Sleep -Seconds 3
    $env:PGPASSWORD = ""
    $escaped = $NewPassword -replace "'", "''"
    & $psql -h 127.0.0.1 -p $Port -U postgres -c "ALTER USER postgres WITH PASSWORD '$escaped';"
    if ($LASTEXITCODE -ne 0) {
        Copy-Item -LiteralPath $hbaBackup -Destination $hbaPath -Force
        throw "ALTER USER postgres failed during password repair"
    }
    Copy-Item -LiteralPath $hbaBackup -Destination $hbaPath -Force
    Remove-Item -LiteralPath $hbaBackup -Force
    Restart-Service CheckpointPostgres -Force -ErrorAction Stop
    Start-Sleep -Seconds 3
    Write-InitLog "Repair-PostgresSuperuserPassword: ok"
}

function Initialize-FreshPgDataCluster {
    Write-InitLog "Initialize-FreshPgDataCluster: begin"
    $stopSvc = Get-Service CheckpointPostgres -ErrorAction SilentlyContinue
    if ($stopSvc -and $stopSvc.Status -eq 'Running') {
        Stop-Service CheckpointPostgres -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $PgData) {
        & $pgCtl stop -D $PgData -m immediate -w 2>$null | Out-Null
    }
    & sc.exe delete CheckpointPostgres 2>$null | Out-Null
    Start-Sleep -Seconds 2
    if (Test-Path -LiteralPath $PgData) {
        $parent = Split-Path $PgData -Parent
        $staleName = "pgdata-stale-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
        $stalePath = Join-Path $parent $staleName
        Move-Item -LiteralPath $PgData -Destination $stalePath
        Write-InitLog "Initialize-FreshPgDataCluster: moved stale data to $stalePath"
    }
    New-Item -ItemType Directory -Force -Path $PgData | Out-Null
    $pwFile = Join-Path $env:TEMP "checkpoint-initdb-pw.txt"
    Set-Content -LiteralPath $pwFile -Value $SuperPassword -NoNewline -Encoding ASCII
    & $initdb -U postgres -A scram-sha-256 -E UTF8 -D $PgData --pwfile=$pwFile
    Remove-Item -LiteralPath $pwFile -Force
    if ($LASTEXITCODE -ne 0) {
        throw "initdb failed after stale pgdata rename"
    }
    Set-PostgresClusterConf
    & $pgCtl register -N CheckpointPostgres -D $PgData -S auto
    if ($LASTEXITCODE -ne 0) {
        throw "pg_ctl register failed after fresh cluster"
    }
    Start-Service CheckpointPostgres -ErrorAction Stop
    Start-Sleep -Seconds 3
    Write-InitLog "Initialize-FreshPgDataCluster: ok"
}

if (-not (Test-Path -LiteralPath $initdb)) {
    throw "initdb missing at $initdb"
}

Write-InitLog "Initialize-CheckpointPostgres: begin port=$Port"
$pgVersionFile = Join-Path $PgData "PG_VERSION"
if (-not (Test-Path -LiteralPath $pgVersionFile)) {
    if (-not (Test-Path -LiteralPath $PgData)) {
        New-Item -ItemType Directory -Force -Path $PgData | Out-Null
    }
    $pwFile = Join-Path $env:TEMP "checkpoint-initdb-pw.txt"
    Set-Content -LiteralPath $pwFile -Value $SuperPassword -NoNewline -Encoding ASCII
    & $initdb -U postgres -A scram-sha-256 -E UTF8 -D $PgData --pwfile=$pwFile
    Remove-Item -LiteralPath $pwFile -Force
    if ($LASTEXITCODE -ne 0) {
        throw "initdb failed"
    }
    Write-InitLog "initdb: new cluster"
}

Set-PostgresClusterConf

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

if (-not (Test-PostgresSuperuserLogin $SuperPassword)) {
    Write-InitLog "superuser login failed with install secrets; attempting repair"
    try {
        Repair-PostgresSuperuserPassword $SuperPassword
    } catch {
        Write-InitLog "password repair failed: $_"
    }
}

if (-not (Test-PostgresSuperuserLogin $SuperPassword)) {
    if (Test-Path -LiteralPath $pgVersionFile) {
        Initialize-FreshPgDataCluster
    }
    if (-not (Test-PostgresSuperuserLogin $SuperPassword)) {
        throw "postgres superuser password could not be aligned with install secrets"
    }
}

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
Write-InitLog "Initialize-CheckpointPostgres: ok"