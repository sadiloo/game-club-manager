# Checkpoint server restore helper (B). Persian guidance on console; no UI.
param(
  [Parameter(Mandatory)][string]$DumpPath,
  [string]$ConnectionString = "Host=127.0.0.1;Port=5433;Database=checkpoint;Username=checkpoint;Password=checkpoint"
)

$ErrorActionPreference = "Stop"
Write-Host "پشتیبان امنیتی قبل از بازیابی …"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$safeDir = Join-Path $env:ProgramData "Checkpoint\backups"
New-Item -ItemType Directory -Force -Path $safeDir | Out-Null
$pre = Join-Path $safeDir "pre-restore-$stamp.dump"
& pg_dump -Fc -f $pre 2>$null
Write-Host "خدمت سرور متوقف می‌شود …"
Stop-Service CheckpointServer -ErrorAction SilentlyContinue
Write-Host "بازیابی از $DumpPath …"
& pg_restore --clean --if-exists -d $ConnectionString $DumpPath
Write-Host "سرور دوباره روشن می‌شود."
Start-Service CheckpointServer -ErrorAction SilentlyContinue
Write-Host "تمام شد."