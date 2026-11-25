# Checkpoint server restore helper (B). Persian guidance on console; no UI.
param(
    [Parameter(Mandatory)][string]$DumpPath,
    [string]$ServerJsonPath = "$env:ProgramData\Checkpoint\config\server.json"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "CheckpointDbConfig.ps1")

if (-not (Test-Path -LiteralPath $DumpPath)) {
    Write-Error "Dump file not found: $DumpPath"
    exit 1
}

$cfg = Read-CheckpointServerJsonConfig -ServerJsonPath $ServerJsonPath
$safeDir = Join-Path $env:ProgramData "Checkpoint\backups"
if (-not (Test-Path -LiteralPath $safeDir)) {
    New-Item -ItemType Directory -Force -Path $safeDir | Out-Null
}

Write-Host "پشتیبان امنیتی قبل از بازیابی …"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$pre = Join-Path $safeDir "pre-restore-$stamp.dump"

$env:PGPASSWORD = $cfg.Password
$pgDump = Get-CheckpointPgExecutable -Name "pg_dump"
& $pgDump -Fc -h $cfg.Host -p $cfg.Port -U $cfg.Username $cfg.Database -f $pre
if ($LASTEXITCODE -ne 0) {
    Write-Error "Safety pg_dump failed with exit code $LASTEXITCODE; restore aborted."
    exit 1
}

try {
    Test-PostgresDumpReadable -DumpPath $pre
}
catch {
    Write-Error "Safety dump is unreadable ($($_.Exception.Message)); restore aborted before --clean."
    exit 1
}

try {
    Test-PostgresDumpReadable -DumpPath $DumpPath
}
catch {
    Write-Error "Input dump is unreadable ($($_.Exception.Message)); restore aborted before --clean."
    exit 1
}

Write-Host "خدمت سرور متوقف می‌شود …"
Stop-Service CheckpointServer -ErrorAction SilentlyContinue

Write-Host "بازیابی از $DumpPath …"
$pgRestore = Get-CheckpointPgExecutable -Name "pg_restore"
& $pgRestore --clean --if-exists -h $cfg.Host -p $cfg.Port -U $cfg.Username -d $cfg.Database $DumpPath
if ($LASTEXITCODE -ne 0) {
    Write-Error "pg_restore failed with exit code $LASTEXITCODE"
    exit 1
}

Write-Host "سرور دوباره روشن می‌شود."
Start-Service CheckpointServer -ErrorAction SilentlyContinue
Write-Host "تمام شد."
Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue