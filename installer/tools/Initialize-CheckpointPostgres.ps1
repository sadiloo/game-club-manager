# First-install PostgreSQL init for Checkpoint (port 5433, localhost only).
param(
    [string] $PgRoot = "C:\Program Files\Checkpoint\PostgreSQL",
    [string] $PgData = "C:\ProgramData\Checkpoint\pgdata",
    [int] $Port = 5433,
    [string] $SuperPassword,
    [switch] $ReinstallKeptCluster
)

$ErrorActionPreference = "Stop"
$initdb = Join-Path $PgRoot "bin\initdb.exe"
$pgCtl = Join-Path $PgRoot "bin\pg_ctl.exe"
$psql = Join-Path $PgRoot "bin\psql.exe"
$initLog = "C:\ProgramData\Checkpoint\logs\postgres-init.log"

function Invoke-PostgresNative {
    param(
        [string] $FilePath,
        [string[]] $ArgumentList
    )
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $null = & $FilePath @ArgumentList 2>&1
        return $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $old
    }
}

function Write-InitLog([string] $Message) {
    $logDir = Split-Path $initLog -Parent
    if (-not (Test-Path -LiteralPath $logDir)) {
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    }
    Add-Content -LiteralPath $initLog -Value ("{0} {1}" -f (Get-Date -Format "o"), $Message)
}

function Test-PostgresSuperuserLogin([string] $Password) {
    $env:PGPASSWORD = $Password
    $code = Invoke-PostgresNative -FilePath $psql -ArgumentList @("-h", "127.0.0.1", "-p", "$Port", "-U", "postgres", "-tc", "SELECT 1")
    return ($code -eq 0)
}

function Wait-PostgresSuperuserLogin([string] $Password, [int] $TimeoutSeconds = 45) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-PostgresSuperuserLogin $Password) {
            return $true
        }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Set-PostgresClusterConf {
    $conf = Join-Path $PgData "postgresql.conf"
    if (-not (Test-Path -LiteralPath $conf)) { return }
    $text = Get-Content -LiteralPath $conf -Raw
    if ($text -match "(?m)^port\s*=\s*\d+") {
        $text = $text -replace "(?m)^port\s*=\s*\d+", "port = $Port"
    } elseif ($text -match "(?m)^#\s*port\s*=\s*\d+") {
        $text = $text -replace "(?m)^#\s*port\s*=\s*\d+", "port = $Port"
    } else {
        $text += "`nport = $Port`n"
    }
    if ($text -match "(?m)^listen_addresses\s*=") {
        $text = $text -replace "(?m)^listen_addresses\s*=\s*'[^']*'", "listen_addresses = '127.0.0.1'"
    } elseif ($text -match "(?m)^#\s*listen_addresses\s*=") {
        $text = $text -replace "(?m)^#\s*listen_addresses\s*=\s*'[^']*'", "listen_addresses = '127.0.0.1'"
    } else {
        $text += "`nlisten_addresses = '127.0.0.1'`n"
    }
    Set-Content -LiteralPath $conf -Value $text -NoNewline
    Write-InitLog "Set-PostgresClusterConf: port=$Port listen=127.0.0.1"
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
    $alterCode = Invoke-PostgresNative -FilePath $psql -ArgumentList @("-h", "127.0.0.1", "-p", "$Port", "-U", "postgres", "-c", "ALTER USER postgres WITH PASSWORD '$escaped';")
    if ($alterCode -ne 0) {
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
    $initCode = Invoke-PostgresNative -FilePath $initdb -ArgumentList @("-U", "postgres", "-A", "scram-sha-256", "-E", "UTF8", "-D", $PgData, "--pwfile=$pwFile")
    Remove-Item -LiteralPath $pwFile -Force
    if ($initCode -ne 0) {
        throw "initdb failed after stale pgdata rename"
    }
    Set-PostgresClusterConf
    $regCode = Invoke-PostgresNative -FilePath $pgCtl -ArgumentList @("register", "-N", "CheckpointPostgres", "-D", $PgData, "-S", "auto")
    if ($regCode -ne 0) {
        throw "pg_ctl register failed after fresh cluster"
    }
    Start-Service CheckpointPostgres -ErrorAction Stop
    Start-Sleep -Seconds 3
    Write-InitLog "Initialize-FreshPgDataCluster: ok"
}

if (-not (Test-Path -LiteralPath $initdb)) {
    throw "initdb missing at $initdb"
}

Write-InitLog "Initialize-CheckpointPostgres: begin port=$Port reinstallKept=$($ReinstallKeptCluster.IsPresent)"
$pgVersionFile = Join-Path $PgData "PG_VERSION"

if ($ReinstallKeptCluster) {
    if (-not (Test-Path -LiteralPath $pgVersionFile)) {
        throw "ReinstallKeptCluster: PG_VERSION missing"
    }
    $probeScript = Join-Path $PSScriptRoot "Test-CheckpointPgDataCluster.ps1"
    if (Test-Path -LiteralPath $probeScript) {
        $probe = (& $probeScript -PgData $PgData -PgRoot $PgRoot -ExpectedMajor 16).Trim()
        if ($probe -ne "VALID") {
            throw "ReinstallKeptCluster: cluster probe $probe"
        }
    }
    Set-PostgresClusterConf
    $svcKept = Get-Service CheckpointPostgres -ErrorAction SilentlyContinue
    if ($svcKept) {
        if ($svcKept.Status -ne "Running") {
            Start-Service CheckpointPostgres -ErrorAction Stop
        }
    } else {
        $regCode = Invoke-PostgresNative -FilePath $pgCtl -ArgumentList @("register", "-N", "CheckpointPostgres", "-D", $PgData, "-S", "auto")
        if ($regCode -ne 0) {
            throw "pg_ctl register CheckpointPostgres failed exit $regCode (reinstall-kept)"
        }
        Start-Service CheckpointPostgres -ErrorAction Stop
    }
    Start-Sleep -Seconds 3
    if (-not (Wait-PostgresSuperuserLogin $SuperPassword 45)) {
        if (-not (Test-PostgresSuperuserLogin $SuperPassword)) {
            Write-InitLog "ReinstallKeptCluster: superuser login failed; attempting repair"
            try {
                Repair-PostgresSuperuserPassword $SuperPassword
            } catch {
                Write-InitLog "ReinstallKeptCluster: password repair failed: $_"
                throw
            }
        }
    }
    if (-not (Wait-PostgresSuperuserLogin $SuperPassword 15)) {
        throw "ReinstallKeptCluster: superuser password could not be validated"
    }
    $existsKept = $false
    $env:PGPASSWORD = $SuperPassword
    $oldKept = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    & $psql -h 127.0.0.1 -p $Port -U postgres -tc "SELECT 1 FROM pg_database WHERE datname='checkpoint'" | ForEach-Object {
        if ($_ -match "1") { $existsKept = $true }
    }
    if (-not $existsKept) {
        $createCode = Invoke-PostgresNative -FilePath $psql -ArgumentList @("-h", "127.0.0.1", "-p", "$Port", "-U", "postgres", "-c", "CREATE DATABASE checkpoint;")
        if ($createCode -ne 0) {
            $ErrorActionPreference = $oldKept
            throw "CREATE DATABASE checkpoint failed (reinstall-kept)"
        }
    }
    $ErrorActionPreference = $oldKept
    Write-InitLog "Initialize-CheckpointPostgres: ok (reinstall-kept)"
    exit 0
}
function Invoke-InitDbCluster {
    if (-not (Test-Path -LiteralPath $PgData)) {
        New-Item -ItemType Directory -Force -Path $PgData | Out-Null
    }
    $pwFile = Join-Path $env:TEMP "checkpoint-initdb-pw.txt"
    Set-Content -LiteralPath $pwFile -Value $SuperPassword -NoNewline -Encoding ASCII
    $initCode = Invoke-PostgresNative -FilePath $initdb -ArgumentList @("-U", "postgres", "-A", "scram-sha-256", "-E", "UTF8", "-D", $PgData, "--pwfile=$pwFile")
    Remove-Item -LiteralPath $pwFile -Force
    return $initCode
}

$createdNewCluster = $false
if (-not (Test-Path -LiteralPath $pgVersionFile)) {
    $initCode = Invoke-InitDbCluster
    if ($initCode -ne 0) {
        Write-InitLog "initdb failed exit $initCode on $PgData; moving aside and retrying"
        if (Test-Path -LiteralPath $PgData) {
            $parent = Split-Path $PgData -Parent
            $staleName = "pgdata-stale-init-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
            Move-Item -LiteralPath $PgData -Destination (Join-Path $parent $staleName)
        }
        $initCode = Invoke-InitDbCluster
        if ($initCode -ne 0) {
            throw "initdb failed after move-aside (exit $initCode)"
        }
    }
    Write-InitLog "initdb: new cluster"
    $createdNewCluster = $true
}

Set-PostgresClusterConf

if ((Test-Path -LiteralPath $pgVersionFile) -and -not (Get-Service CheckpointPostgres -ErrorAction SilentlyContinue) -and -not $createdNewCluster) {
    Write-InitLog "pgdata present but CheckpointPostgres service missing; reinitializing cluster"
    Initialize-FreshPgDataCluster
    $pgVersionFile = Join-Path $PgData "PG_VERSION"
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
    $regCode = Invoke-PostgresNative -FilePath $pgCtl -ArgumentList @("register", "-N", "CheckpointPostgres", "-D", $PgData, "-S", "auto")
    if ($regCode -ne 0) {
        throw "pg_ctl register CheckpointPostgres failed exit $regCode"
    }
}

try {
    Start-Service CheckpointPostgres -ErrorAction Stop
    if ((Get-Service CheckpointPostgres).Status -ne 'Running') {
        throw "CheckpointPostgres not Running after Start-Service"
    }
} catch {
    Write-InitLog "Start-Service CheckpointPostgres failed: $_; reinitializing cluster"
    Initialize-FreshPgDataCluster
    Start-Service CheckpointPostgres -ErrorAction Stop
    if ((Get-Service CheckpointPostgres).Status -ne 'Running') {
        throw "CheckpointPostgres failed to reach Running after cluster reinit"
    }
}
Start-Sleep -Seconds 3

if (-not (Wait-PostgresSuperuserLogin $SuperPassword)) {
    if (-not $createdNewCluster) {
        Write-InitLog "superuser login failed with install secrets; attempting repair"
        try {
            Repair-PostgresSuperuserPassword $SuperPassword
        } catch {
            Write-InitLog "password repair failed: $_"
        }
    } else {
        Write-InitLog "superuser login failed on fresh cluster after wait"
    }
}

if (-not (Wait-PostgresSuperuserLogin $SuperPassword 15)) {
    if (-not $createdNewCluster -and (Test-Path -LiteralPath $pgVersionFile)) {
        Initialize-FreshPgDataCluster
        $createdNewCluster = $true
        if (-not (Wait-PostgresSuperuserLogin $SuperPassword 45)) {
            throw "postgres superuser password could not be aligned with install secrets"
        }
    } else {
        throw "postgres superuser password could not be aligned with install secrets"
    }
}

$exists = $false
$env:PGPASSWORD = $SuperPassword
$old = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& $psql -h 127.0.0.1 -p $Port -U postgres -tc "SELECT 1 FROM pg_database WHERE datname='checkpoint'" | ForEach-Object {
    if ($_ -match "1") { $exists = $true }
}
if (-not $exists) {
    $createCode = Invoke-PostgresNative -FilePath $psql -ArgumentList @("-h", "127.0.0.1", "-p", "$Port", "-U", "postgres", "-c", "CREATE DATABASE checkpoint;")
    if ($createCode -ne 0) {
        $ErrorActionPreference = $old
        throw "CREATE DATABASE checkpoint failed"
    }
}
$ErrorActionPreference = $old
Write-InitLog "Initialize-CheckpointPostgres: ok"