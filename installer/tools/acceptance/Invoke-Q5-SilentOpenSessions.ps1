param(
    [string] $LowSetup,
    [string] $HighSetup,
    [string] $AccountsFile,
    [string] $LowVersion,
    [string] $LogsDir
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

Write-Host "Q5: silent upgrade with open sessions must abort"
& (Join-Path $PSScriptRoot "Reset-InstallerAcceptanceMachine.ps1")
Install-ServerSetup -LegLabel "q5-open-sessions-low" -Exe $LowSetup -Accounts $AccountsFile -FromVer ""
. (Join-Path $PSScriptRoot "Seed-InstallerAcceptanceData.ps1") -AccountsFile $AccountsFile -WithOpenSessions

$logPath = Join-Path $LogsDir "acceptance-setup-q5-silent-open-sessions.log"
if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }
$env:CHECKPOINT_UPGRADE_FROM = $LowVersion
$result = Start-CheckpointServerSetup -LegLabel "q5-silent-open-sessions" -Exe $HighSetup -ArgumentList @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
    "/LOG=`"$logPath`""
) -LogPath $logPath -ExpectExitCode $script:CheckpointSetupExitOpenSessionsSilent

if ($result.ExitCode -ne $script:CheckpointSetupExitOpenSessionsSilent) {
    throw "Q5: expected exit $($script:CheckpointSetupExitOpenSessionsSilent); got $($result.ExitCode)"
}
if (-not (Select-String -LiteralPath $logPath -Pattern "CHECKPOINT_OPEN_SESSIONS_SILENT" -SimpleMatch)) {
    throw "Q5: Inno log missing CHECKPOINT_OPEN_SESSIONS_SILENT"
}

Write-Host "Q5: ok"