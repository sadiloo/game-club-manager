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

$serverJson = "C:\ProgramData\Checkpoint\config\server.json"
$cfg = Read-CheckpointServerJsonConfig -ServerJsonPath $serverJson
$env:PGPASSWORD = $cfg.Password
$psql = Get-CheckpointPgExecutable -Name "psql"

$beforeRaw = & $psql -h $cfg.Host -p $cfg.Port -U $cfg.Username -d $cfg.Database -tAc @"
SELECT "HourlyToman" FROM rate_lines WHERE "GroupId"='ps5' AND "PlayMode"='Offline' AND "PlayerCount"=1 AND "IsSpecialBand"=false LIMIT 1;
"@ 2>&1
if ($LASTEXITCODE -ne 0) { throw "B-3-restore: read ps5 rate failed: $beforeRaw" }
$beforeText = ([string]($beforeRaw | Out-String)).Trim()
if ($beforeText -notmatch '^\d+$') {
    throw "B-3-restore: ps5 Offline 1-player rate row missing or not numeric before tamper (got '$beforeText')"
}
$dumpedValue = [long]$beforeText
$tampered = $dumpedValue + 37
$upd = & $psql -h $cfg.Host -p $cfg.Port -U $cfg.Username -d $cfg.Database -v ON_ERROR_STOP=1 -c @"
UPDATE rate_lines SET "HourlyToman"=$tampered WHERE "GroupId"='ps5' AND "PlayMode"='Offline' AND "PlayerCount"=1 AND "IsSpecialBand"=false;
"@ 2>&1
if ($LASTEXITCODE -ne 0) { throw "B-3-restore: tamper rate failed: $upd" }
if ((([string]($upd | Out-String)).Trim()) -ne "UPDATE 1") {
    throw "B-3-restore: tamper must update exactly one ps5 rate row; psql said '$upd'"
}

$restoreLog = Join-Path $LogsDir "b3-restore.log"
$restoreScript = "C:\Program Files\Checkpoint\Server\tools\restore.ps1"
if (-not (Test-Path -LiteralPath $restoreScript)) {
    $restoreScript = Join-Path (Split-Path $PSScriptRoot -Parent) "restore.ps1"
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $restoreScript -DumpPath $latest.FullName *> $restoreLog
if ($LASTEXITCODE -ne 0) {
    throw "B-3-restore: restore.ps1 failed exit $LASTEXITCODE (see $restoreLog)"
}

$afterRaw = & $psql -h $cfg.Host -p $cfg.Port -U $cfg.Username -d $cfg.Database -tAc @"
SELECT "HourlyToman" FROM rate_lines WHERE "GroupId"='ps5' AND "PlayMode"='Offline' AND "PlayerCount"=1 AND "IsSpecialBand"=false LIMIT 1;
"@ 2>&1
if ($LASTEXITCODE -ne 0) { throw "B-3-restore: read rate after restore failed: $afterRaw" }
$afterText = ([string]($afterRaw | Out-String)).Trim()
if ($afterText -notmatch '^\d+$') {
    throw "B-3-restore: ps5 rate row missing or not numeric after restore (got '$afterText')"
}
$restored = [long]$afterText
if ($restored -ne $dumpedValue) {
    throw "B-3-restore: rate not restored to dump value; expected $dumpedValue got $restored (tampered was $tampered)"
}

Assert-InstallerServerServicesRunning
Assert-InstallerServerHealthOk
Write-Host "B-3-restore: ok dump=$($latest.Name) ps5_rate=$restored"