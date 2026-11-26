param(
    [string] $ServerSetupHooks,
    [string] $ServerSetup,
    [string] $AccountsFile,
    [string] $LowSetup,
    [string] $LowVersion,
    [string] $HighVersion,
    [string] $LogsDir = "C:\ProgramData\Checkpoint\logs"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")

Write-Host "R8f-1 fresh: postinstall fail hook (start phase, hooks artifact)"
& (Join-Path $PSScriptRoot "Reset-InstallerAcceptanceMachine.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null
$r8fFreshLog = Join-Path $LogsDir "acceptance-setup-r8f-postinstall-fail-fresh.log"
if (Test-Path -LiteralPath $r8fFreshLog) { Remove-Item -LiteralPath $r8fFreshLog -Force }
$setupArgs = @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
    "/ACCOUNTS=$AccountsFile",
    "/SKIPSESSIONCHECK=1",
    "/TESTFAILPHASE=start",
    "/LOG=`"$r8fFreshLog`""
)
Start-CheckpointServerSetup -LegLabel "r8f-postinstall-fail-fresh" -Exe $ServerSetupHooks -ArgumentList $setupArgs `
    -TimeoutSeconds 900 -LogPath $r8fFreshLog -ExpectExitCode $script:CheckpointSetupExitFailStart `
        -IntendedPostinstallFailPhase "start" | Out-Null
# Item 7: a failed FIRST install leaves both services stopped (Setup logs it; PowerShell logs its own cleanup).
if (-not (Select-String -LiteralPath $r8fFreshLog -Pattern "CHECKPOINT_SETUP_FRESH_ABORT_SERVICES_STOPPED services=CheckpointServer,CheckpointPostgres" -SimpleMatch)) {
    throw "R8f-1 fresh: Inno log missing CHECKPOINT_SETUP_FRESH_ABORT_SERVICES_STOPPED"
}
if (-not (Select-String -LiteralPath $r8fFreshLog -Pattern "CHECKPOINT_POSTINSTALL_ABORT_SERVICES_STOPPED" -SimpleMatch)) {
    throw "R8f-1 fresh: Inno log missing the PowerShell CHECKPOINT_POSTINSTALL_ABORT_SERVICES_STOPPED line"
}
foreach ($svcName in @("CheckpointServer", "CheckpointPostgres")) {
    $svc = Get-Service $svcName -ErrorAction SilentlyContinue
    if (-not $svc) { throw "R8f-1 fresh: $svcName service missing (the start phase runs after services were registered)" }
    if ($svc.Status -ne "Stopped") {
        try { $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60)) } catch { Write-Host "R8f-1 fresh: $svcName WaitForStatus: $($_.Exception.Message)" }
        $svc.Refresh()
    }
    if ($svc.Status -ne "Stopped") { throw "R8f-1 fresh: $svcName must be Stopped after a failed first install; status=$($svc.Status)" }
    Write-Host "R8f-1 fresh: $svcName Stopped"
}

Write-Host "R8f-1 upgrade: postinstall fail hook (upgrade-backup)"
& (Join-Path $PSScriptRoot "Reset-InstallerAcceptanceMachine.ps1")
$r8fSeedLog = Join-Path $LogsDir "acceptance-setup-r8f-postinstall-fail-upgrade-seed.log"
if (Test-Path -LiteralPath $r8fSeedLog) { Remove-Item -LiteralPath $r8fSeedLog -Force }
$setupArgsLow = @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
    "/ACCOUNTS=$AccountsFile",
    "/LOG=`"$r8fSeedLog`""
)
Start-CheckpointServerSetup -LegLabel "r8f-postinstall-fail-upgrade-seed" -Exe $LowSetup -ArgumentList $setupArgsLow `
    -TimeoutSeconds 2700 -LogPath $r8fSeedLog | Out-Null

$r8fMigrationsBefore = Get-InstallerMigrationHistoryFingerprint
$r8fUpgradeStart = Get-Date
$r8fUpgradeLog = Join-Path $LogsDir "acceptance-setup-r8f-postinstall-fail-upgrade.log"
if (Test-Path -LiteralPath $r8fUpgradeLog) { Remove-Item -LiteralPath $r8fUpgradeLog -Force }
$upgradeArgs = @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
    "/SKIPSESSIONCHECK=1",
    "/LOG=`"$r8fUpgradeLog`""
)
$upgradeArgs += "/TESTFAILPHASE=upgrade-backup"
Start-CheckpointServerSetup -LegLabel "r8f-postinstall-fail-upgrade" -Exe $ServerSetupHooks -ArgumentList $upgradeArgs `
    -TimeoutSeconds 900 -LogPath $r8fUpgradeLog -ExpectExitCode $script:CheckpointSetupExitFailUpgradeBackup `
        -IntendedPostinstallFailPhase "upgrade-backup" | Out-Null

Assert-InstallerUpgradeLeftStoppedNoDump -LegLabel "R8f-1 upgrade (upgrade-backup failed)" -InnoLogPath $r8fUpgradeLog `
    -MigrationsBefore $r8fMigrationsBefore -LegStart $r8fUpgradeStart
Write-Host "R8f-1: ok"