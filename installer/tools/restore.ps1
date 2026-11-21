# Checkpoint server restore helper (B). Persian guidance on console; no UI.
param(
    [Parameter(Mandatory)][string]$DumpPath,
    [string]$ServerJsonPath = "C:\ProgramData\Checkpoint\config\server.json"
)

$ErrorActionPreference = "Stop"
$connScript = Join-Path $PSScriptRoot "Read-ServerJsonConnection.ps1"
if (-not (Test-Path -LiteralPath $connScript)) {
    throw "Read-ServerJsonConnection.ps1 missing"
}
$conn = & $connScript -ServerJsonPath $ServerJsonPath

Write-Host "پشتیبان امنیتی قبل از بازیابی …"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$safeDir = Join-Path $env:ProgramData "Checkpoint\backups"
New-Item -ItemType Directory -Force -Path $safeDir | Out-Null
$pre = Join-Path $safeDir "pre-restore-$stamp.dump"

$env:PGPASSWORD = $conn.Password
& pg_dump -Fc -h $conn.Host -p $conn.Port -U $conn.Username -d $conn.Database -f $pre
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $pre) -or (Get-Item -LiteralPath $pre).Length -eq 0) {
    throw "safety pg_dump failed before restore"
}

Write-Host "خدمت سرور متوقف می‌شود …"
Stop-Service CheckpointServer -ErrorAction SilentlyContinue

Write-Host "بازیابی از $DumpPath …"
& pg_restore --clean --if-exists -h $conn.Host -p $conn.Port -U $conn.Username -d $conn.Database $DumpPath
if ($LASTEXITCODE -ne 0) {
    throw "pg_restore failed with exit $LASTEXITCODE"
}

Write-Host "سرور دوباره روشن می‌شود."
Start-Service CheckpointServer -ErrorAction Stop
Write-Host "تمام شد."