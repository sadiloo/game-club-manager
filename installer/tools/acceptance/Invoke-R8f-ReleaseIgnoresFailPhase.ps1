param(
    [string] $ServerSetup,
    [string] $AccountsFile,
    [string] $LogsDir
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

Write-Host "R8f-release-hook: release installer must ignore .internal-test-fail-phase"
& (Join-Path $PSScriptRoot "Reset-InstallerAcceptanceMachine.ps1")
Set-AcceptancePostinstallFailPhase -Phase "start"
try {
    $logPath = Join-Path $LogsDir "acceptance-setup-r8f-release-ignore-failphase.log"
    if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }
    $result = Start-CheckpointServerSetup -LegLabel "r8f-release-ignore-failphase" -Exe $ServerSetup -ArgumentList @(
        "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ACCOUNTS=$AccountsFile",
        "/LOG=`"$logPath`""
    ) -LogPath $logPath
    if ($result.ExitCode -ne 0) {
        throw "R8f-release-hook: release setup must succeed with stale fail-phase file; exit $($result.ExitCode)"
    }
    Assert-InstallerServerServicesRunning
    $server = Get-Service CheckpointServer -ErrorAction SilentlyContinue
    if ($server.Status -ne "Running") {
        throw "R8f-release-hook: CheckpointServer not running (fail phase must be ignored)"
    }
} finally {
    Clear-AcceptancePostinstallFailPhase
}

Write-Host "R8f-release-hook: ok"