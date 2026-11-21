# Checkpoint daily backup (B-3). Dumps are kept forever; this script only writes new ones.
param(
    [string] $PrimaryBackupDir = "C:\ProgramData\Checkpoint\backups",
    [string] $SecondaryBackupDir = "",
    [string] $LogsDir = "C:\ProgramData\Checkpoint\logs"
)

$ErrorActionPreference = "Stop"
$serverJson = "C:\ProgramData\Checkpoint\config\server.json"
$connScript = Join-Path $PSScriptRoot "Read-ServerJsonConnection.ps1"
if (-not (Test-Path -LiteralPath $connScript)) {
    throw "Read-ServerJsonConnection.ps1 missing"
}
$conn = & $connScript -ServerJsonPath $serverJson

New-Item -ItemType Directory -Force -Path $PrimaryBackupDir | Out-Null
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

$stamp = Get-Date -Format "yyyyMMdd-HHmm"
$dump = Join-Path $PrimaryBackupDir "checkpoint-$stamp.dump"
$logPath = Join-Path $LogsDir "backup.log"

$env:PGPASSWORD = $conn.Password
& pg_dump -Fc -h $conn.Host -p $conn.Port -U $conn.Username -d $conn.Database -f $dump
if ($LASTEXITCODE -ne 0) {
    $line = "$(Get-Date -Format o) backup error pg_dump exit=$LASTEXITCODE"
    Add-Content -Path $logPath -Value $line
    throw "pg_dump failed with exit $LASTEXITCODE"
}
if (-not (Test-Path -LiteralPath $dump) -or (Get-Item -LiteralPath $dump).Length -eq 0) {
    Add-Content -Path $logPath -Value "$(Get-Date -Format o) backup error zero-byte dump"
    throw "pg_dump produced empty file"
}

if ($SecondaryBackupDir) {
    try {
        New-Item -ItemType Directory -Force -Path $SecondaryBackupDir | Out-Null
        Copy-Item -LiteralPath $dump -Destination (Join-Path $SecondaryBackupDir (Split-Path $dump -Leaf)) -Force
    }
    catch {
        $flag = Join-Path $LogsDir "backup-secondary-missing.flag"
        Set-Content -LiteralPath $flag -Value "پوشهٔ پشتیبان دوم در دسترس نیست."
        Add-Content -Path $logPath -Value "$(Get-Date -Format o) backup secondary copy failed: $_"
    }
}

Add-Content -Path $logPath -Value "$(Get-Date -Format o) backup ok $dump"