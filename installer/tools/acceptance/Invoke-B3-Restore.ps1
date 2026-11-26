param(
    [string] $LogsDir
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
. (Join-Path (Split-Path $PSScriptRoot -Parent) "CheckpointDbConfig.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

$backupDir = "C:\ProgramData\Checkpoint\backups"
$latest = Get-ChildItem -LiteralPath $backupDir -Filter "checkpoint-*.dump" -ErrorAction Stop |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $latest) { throw "B-3-restore: no checkpoint dump in $backupDir" }

$cfg = Read-CheckpointServerJsonConfig
$table = "b3_restore_probe_" + ([guid]::NewGuid().ToString("N").Substring(0, 8))
$env:PGPASSWORD = $cfg.Password
$psql = Get-CheckpointPgExecutable -Name "psql"
& $psql -h $cfg.Host -p $cfg.Port -U $cfg.Username -d $cfg.Database -v ON_ERROR_STOP=1 -c "CREATE TABLE `"$table`" (id int primary key);" 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "B-3-restore: create probe table failed" }

$restoreLog = Join-Path $LogsDir "b3-restore.log"
$restoreScript = "C:\Program Files\Checkpoint\Server\tools\restore.ps1"
if (-not (Test-Path -LiteralPath $restoreScript)) {
    $restoreScript = Join-Path (Split-Path $PSScriptRoot -Parent) "restore.ps1"
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $restoreScript -DumpPath $latest.FullName *> $restoreLog
if ($LASTEXITCODE -ne 0) {
    throw "B-3-restore: restore.ps1 failed exit $LASTEXITCODE (see $restoreLog)"
}

$exists = & $psql -h $cfg.Host -p $cfg.Port -U $cfg.Username -d $cfg.Database -tAc "SELECT to_regclass('public.$table') IS NOT NULL;" 2>&1
if (($exists.Trim()) -eq "t") {
    throw "B-3-restore: probe table still present after restore (restore did not roll back post-dump writes)"
}

Assert-InstallerServerServicesRunning
Assert-InstallerServerHealthOk
Write-Host "B-3-restore: ok dump=$($latest.Name)"