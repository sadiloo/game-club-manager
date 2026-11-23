# Checkpoint daily backup (B-3). Dumps are kept forever; this script only writes new ones.
param(
    [string] $PrimaryBackupDir = "C:\ProgramData\Checkpoint\backups",
    [string] $SecondaryBackupDir = "",
    [string] $LogsDir = "C:\ProgramData\Checkpoint\logs",
    [string] $ServerJsonPath = "$env:ProgramData\Checkpoint\config\server.json"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "CheckpointDbConfig.ps1")

$logPath = Join-Path $LogsDir "backup.log"
function Write-BackupLog([string]$line) {
    if (-not (Test-Path -LiteralPath $LogsDir)) {
        New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null
    }
    Add-Content -Path $logPath -Value "$(Get-Date -Format o) $line"
}

try {
    if (-not (Test-Path -LiteralPath $PrimaryBackupDir)) {
        New-Item -ItemType Directory -Force -Path $PrimaryBackupDir | Out-Null
    }

    $cfg = Read-CheckpointServerJsonConfig -ServerJsonPath $ServerJsonPath
    $stamp = Get-Date -Format "yyyyMMdd-HHmm"
    $dump = Join-Path $PrimaryBackupDir "checkpoint-$stamp.dump"

    $env:PGPASSWORD = $cfg.Password
    & pg_dump -Fc -h $cfg.Host -p $cfg.Port -U $cfg.Username $cfg.Database -f $dump
    if ($LASTEXITCODE -ne 0) {
        throw "pg_dump failed with exit code $LASTEXITCODE"
    }

    Test-PostgresDumpReadable -DumpPath $dump

    if ($SecondaryBackupDir) {
        if (-not (Test-Path -LiteralPath $SecondaryBackupDir)) {
            throw "Secondary backup folder missing: $SecondaryBackupDir"
        }
        Copy-Item -LiteralPath $dump -Destination (Join-Path $SecondaryBackupDir (Split-Path $dump -Leaf))
    }

    Write-BackupLog "backup ok $dump"
}
catch {
    Write-BackupLog "backup error $($_.Exception.Message)"
    throw
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}