param(
    [string] $ServerSetupHooks,
    [string] $ServerSetup,
    [string] $AccountsFile,
    [string] $LowSetup,
    [string] $LowVersion,
    [string] $HighVersion
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")

Write-Host "R8f-1 fresh: postinstall fail hook (start phase)"
& (Join-Path $PSScriptRoot "Reset-InstallerAcceptanceMachine.ps1")
$setupArgs = @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
    "/ACCOUNTS=$AccountsFile",
    "/TESTFAILPHASE=start"
)
Start-CheckpointServerSetup -LegLabel "r8f-postinstall-fail-fresh" -Exe $ServerSetupHooks -ArgumentList $setupArgs `
    -TimeoutSeconds 900 -ExpectExitCode $script:CheckpointSetupExitPostinstallFailed | Out-Null

Write-Host "R8f-1 upgrade: postinstall fail hook (upgrade-backup)"
& (Join-Path $PSScriptRoot "Reset-InstallerAcceptanceMachine.ps1")
$setupArgsLow = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ACCOUNTS=$AccountsFile")
Start-CheckpointServerSetup -LegLabel "r8f-postinstall-fail-upgrade-seed" -Exe $LowSetup -ArgumentList $setupArgsLow -TimeoutSeconds 2700 | Out-Null

$upgradeArgs = @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
    "/SKIPSESSIONCHECK=1",
    "/TESTFAILPHASE=upgrade-backup"
)
Start-CheckpointServerSetup -LegLabel "r8f-postinstall-fail-upgrade" -Exe $ServerSetupHooks -ArgumentList $upgradeArgs `
    -TimeoutSeconds 900 -ExpectExitCode $script:CheckpointSetupExitPostinstallFailed | Out-Null

if (-not (Get-Service CheckpointServer -ErrorAction SilentlyContinue)) {
    throw "R8f-1 upgrade: CheckpointServer service missing after failed upgrade (DeinitializeSetup restore expected)"
}
Write-Host "R8f-1: ok"