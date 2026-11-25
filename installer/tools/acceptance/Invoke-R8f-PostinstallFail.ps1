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

function Set-R8fPostinstallFailHook {
    param([string] $Phase)
    $env:CHECKPOINT_INTERNAL_TEST_HOOKS = "1"
    $env:CHECKPOINT_TEST_FAIL_PHASE = $Phase
}

function Clear-R8fPostinstallFailHook {
    Remove-Item Env:CHECKPOINT_INTERNAL_TEST_HOOKS -ErrorAction SilentlyContinue
    Remove-Item Env:CHECKPOINT_TEST_FAIL_PHASE -ErrorAction SilentlyContinue
}

Write-Host "R8f-1 fresh: postinstall fail hook (start phase)"
& (Join-Path $PSScriptRoot "Reset-InstallerAcceptanceMachine.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null
$r8fFreshLog = Join-Path $LogsDir "acceptance-setup-r8f-postinstall-fail-fresh.log"
if (Test-Path -LiteralPath $r8fFreshLog) { Remove-Item -LiteralPath $r8fFreshLog -Force }
$setupArgs = @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
    "/ACCOUNTS=$AccountsFile",
    "/SKIPSESSIONCHECK=1",
    "/LOG=`"$r8fFreshLog`""
)
Set-R8fPostinstallFailHook -Phase "start"
try {
    Start-CheckpointServerSetup -LegLabel "r8f-postinstall-fail-fresh" -Exe $ServerSetup -ArgumentList $setupArgs `
        -TimeoutSeconds 900 -LogPath $r8fFreshLog -ExpectExitCode $script:CheckpointSetupExitPostinstallFailed | Out-Null
} finally {
    Clear-R8fPostinstallFailHook
}

Write-Host "R8f-1 upgrade: postinstall fail hook (upgrade-backup)"
& (Join-Path $PSScriptRoot "Reset-InstallerAcceptanceMachine.ps1")
$setupArgsLow = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ACCOUNTS=$AccountsFile")
Start-CheckpointServerSetup -LegLabel "r8f-postinstall-fail-upgrade-seed" -Exe $LowSetup -ArgumentList $setupArgsLow -TimeoutSeconds 2700 | Out-Null

$r8fUpgradeLog = Join-Path $LogsDir "acceptance-setup-r8f-postinstall-fail-upgrade.log"
if (Test-Path -LiteralPath $r8fUpgradeLog) { Remove-Item -LiteralPath $r8fUpgradeLog -Force }
$upgradeArgs = @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
    "/SKIPSESSIONCHECK=1",
    "/LOG=`"$r8fUpgradeLog`""
)
Set-R8fPostinstallFailHook -Phase "upgrade-backup"
try {
    Start-CheckpointServerSetup -LegLabel "r8f-postinstall-fail-upgrade" -Exe $ServerSetup -ArgumentList $upgradeArgs `
        -TimeoutSeconds 900 -LogPath $r8fUpgradeLog -ExpectExitCode $script:CheckpointSetupExitPostinstallFailed | Out-Null
} finally {
    Clear-R8fPostinstallFailHook
}

if (-not (Get-Service CheckpointServer -ErrorAction SilentlyContinue)) {
    throw "R8f-1 upgrade: CheckpointServer service missing after failed upgrade (DeinitializeSetup restore expected)"
}
Write-Host "R8f-1: ok"