# Checkpoint daily backup (B-3). Dumps are kept forever; this script only writes new ones.
param(
    [string] $PrimaryBackupDir = "C:\ProgramData\Checkpoint\backups",
    [string] $SecondaryBackupDir = "",
    [string] $LogsDir = "C:\ProgramData\Checkpoint\logs"
)

$ErrorActionPreference = "Stop"
$stamp = Get-Date -Format "yyyyMMdd-HHmm"
$dump = Join-Path $PrimaryBackupDir "checkpoint-$stamp.dump"
& pg_dump -Fc -h 127.0.0.1 -p 5433 -U postgres gameclub -f $dump
if ($SecondaryBackupDir) {
    Copy-Item $dump (Join-Path $SecondaryBackupDir (Split-Path $dump -Leaf))
}
Add-Content -Path (Join-Path $LogsDir "backup.log") -Value "$(Get-Date -Format o) backup ok $dump"