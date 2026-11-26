# F6 ruling (R10d): a failed upgrade with no pre-upgrade dump from THIS Setup run must leave CheckpointServer
# stopped (the upgraded server migrates the database on start). Behaviour is pinned by
# scripts/installer-wine/Invoke-WineInstallerLegs.sh and Invoke-BoxPostinstallUpgradeRestore.ps1; this is the CI-side static guard.
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$errors = @()

function Get-FunctionText([string] $Path, [string] $Name) {
    $tokens = $null; $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name }, $true)
    if ($null -eq $fn) { return $null }
    return $fn.Extent.Text
}

$ps = Join-Path $root "installer/tools/server-postinstall.ps1"
$psText = Get-Content -LiteralPath $ps -Raw
$cleanup = Get-FunctionText $ps "Invoke-CheckpointPostinstallAbortCleanup"
if (-not $cleanup) {
    $errors += "server-postinstall.ps1: Invoke-CheckpointPostinstallAbortCleanup missing"
} else {
    $iDump = $cleanup.IndexOf('Get-CheckpointPreUpgradeDumpForThisRun')
    $iNoDump = $cleanup.IndexOf('if (-not $dump)')
    $iLeft = $cleanup.IndexOf('CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED reason=no_preupgrade_backup')
    $iReturn = if ($iLeft -ge 0) { $cleanup.IndexOf('return', $iLeft) } else { -1 }
    $iStart = $cleanup.IndexOf('Start-Service CheckpointServer')
    if ($iDump -lt 0) { $errors += "cleanup does not look up the pre-upgrade dump of this run" }
    if ($iNoDump -lt 0 -or $iNoDump -lt $iDump) { $errors += "cleanup has no 'if (-not `$dump)' branch after the dump lookup" }
    if ($iLeft -lt 0 -or $iLeft -lt $iNoDump) { $errors += "cleanup no-dump branch does not log CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED reason=no_preupgrade_backup" }
    if ($cleanup -notmatch 'LEFT_STOPPED[^\r\n]*action=') { $errors += "LEFT_STOPPED line must name the operator action" }
    if ($iReturn -lt 0 -or $iStart -lt 0 -or $iReturn -gt $iStart) { $errors += "cleanup no-dump branch must return before Start-Service CheckpointServer" }
    if ($cleanup -notmatch 'UPGRADE_RESTORE start CheckpointServer dump=\$dump') { $errors += "restore-with-dump line must name the dump" }
}
$getDump = Get-FunctionText $ps "Get-CheckpointPreUpgradeDumpForThisRun"
if (-not $getDump -or $getDump -notmatch 'Length -le 0') { $errors += "Get-CheckpointPreUpgradeDumpForThisRun must reject a missing/empty dump" }
$iThrowEmpty = $psText.IndexOf('pre-upgrade pg_dump produced no dump')
$iMarker = $psText.IndexOf('Set-CheckpointUtf8NoBomFile -Path $preUpgradeDumpMarker')
if ($iThrowEmpty -lt 0 -or $iMarker -lt 0 -or $iMarker -lt $iThrowEmpty) { $errors += "the dump marker must be written only after pg_dump succeeded and produced a non-empty dump" }

$inc = Get-Content -LiteralPath (Join-Path $root "installer/server/ServerWizard.inc.iss") -Raw
$m = [regex]::Match($inc, '(?s)procedure RestoreCheckpointUpgradeAfterPostinstallFailure;.*?\r?\nend;')
if (-not $m.Success) {
    $errors += "ServerWizard.inc.iss: RestoreCheckpointUpgradeAfterPostinstallFailure missing"
} else {
    $p = $m.Value
    $iCheck = $p.IndexOf('not PreUpgradeDumpExists(Dump)')
    $iLeft = $p.IndexOf('CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED reason=no_preupgrade_backup')
    $iExit = if ($iLeft -ge 0) { $p.IndexOf('Exit;', $iLeft) } else { -1 }
    $iSc = $p.IndexOf("'start CheckpointServer'")
    if ($iCheck -lt 0) { $errors += "Inno restore must check PreUpgradeDumpExists" }
    if ($iLeft -lt $iCheck) { $errors += "Inno restore must log LEFT_STOPPED in the no-dump branch" }
    if ($iExit -lt 0 -or $iSc -lt 0 -or $iExit -gt $iSc) { $errors += "Inno restore no-dump branch must Exit before sc start CheckpointServer" }
}
if ($inc -notmatch 'GPostinstallStarted := True;\s*\r?\n\s*RunCheckpointServerPostinstallPhases') { $errors += "GPostinstallStarted must be set right before RunCheckpointServerPostinstallPhases" }
$iss = Get-Content -LiteralPath (Join-Path $root "installer/server/CheckpointServer.iss") -Raw
if ($iss -notmatch 'ClearPreUpgradeDumpMarker;') { $errors += "CheckpointServer.iss InitializeSetup must clear a stale pre-upgrade dump marker" }

if ($errors.Count -gt 0) {
    foreach ($e in $errors) { Write-Error "UpgradeRestoreNoDumpStatic: $e" -ErrorAction Continue }
    throw "UpgradeRestoreNoDumpStatic: $($errors.Count) rule(s) failed"
}
Write-Host "UpgradeRestoreNoDumpStatic: ok"
