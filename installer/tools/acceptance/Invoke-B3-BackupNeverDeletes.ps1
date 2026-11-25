param(
    [string] $LogsDir
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
. (Join-Path (Split-Path $PSScriptRoot -Parent) "CheckpointDbConfig.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

$backupDir = "C:\ProgramData\Checkpoint\backups"
$handOld = Join-Path $backupDir "hand-made-old.dump"
[System.IO.File]::WriteAllText($handOld, "not a dump", [System.Text.UTF8Encoding]::new($false))

$scriptPath = "C:\Program Files\Checkpoint\Server\tools\backup-daily.ps1"
if (-not (Test-Path $scriptPath)) {
    $scriptPath = (Join-Path (Split-Path $PSScriptRoot -Parent) "backup-daily.ps1")
}

for ($i = 0; $i -lt 16; $i++) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath
    if ($LASTEXITCODE -ne 0) { throw "B-3: backup run $i failed" }
}

$dumps = Get-ChildItem $backupDir -Filter "checkpoint-*.dump"
if ($dumps.Count -lt 16) { throw "B-3: expected >=16 dumps got $($dumps.Count)" }
if (-not (Test-Path $handOld)) { throw "B-3: hand-made file deleted" }

$pgRestore = Get-CheckpointPgExecutable -Name "pg_restore"
foreach ($d in $dumps | Select-Object -First 1) {
    & $pgRestore --list $d.FullName 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "B-3: pg_restore --list failed $($d.Name)" }
}

$xmlPath = Join-Path $env:TEMP "checkpoint-daily-backup-task.xml"
schtasks.exe /Query /TN CheckpointDailyBackup /XML | Set-Content -LiteralPath (Join-Path $LogsDir "b3-task.xml")
$xml = Get-Content (Join-Path $LogsDir "b3-task.xml") -Raw
if ($xml -notmatch "StartWhenAvailable") { throw "B-3: StartWhenAvailable missing" }
if ($xml -notmatch "Iran Standard Time" -and $xml -notmatch "StartBoundary") {
    throw "B-3: task timezone/boundary missing"
}
Import-Module (Join-Path (Split-Path $PSScriptRoot -Parent) "CheckpointInstall.Common.psm1") -Force
$expected = Get-CheckpointDailyBackupStartBoundaryUtc
if ($xml -notmatch [regex]::Escape($expected.Substring(0, 13))) {
    Write-Host "B-3: warning boundary check partial expected~$expected"
}

Write-Host "B-3: ok dumps=$($dumps.Count)"