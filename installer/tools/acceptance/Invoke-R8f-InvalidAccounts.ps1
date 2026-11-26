param(
    [string] $ServerSetup,
    [string] $LogsDir
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

& (Join-Path $PSScriptRoot "Reset-InstallerAcceptanceMachine.ps1")
$badAccounts = Join-Path $LogsDir "accounts-blank-operator.json"
@{
    manager1User = "m1"
    manager1Pin  = "5678"
    manager2User = "m2"
    manager2Pin  = "8765"
    operatorUser = ""
    operatorPin  = "4321"
} | ConvertTo-Json | Set-Content -LiteralPath $badAccounts -Encoding UTF8

$logPath = Join-Path $LogsDir "acceptance-setup-r8f-invalid-accounts.log"
$result = Start-CheckpointServerSetup -LegLabel "r8f-invalid-accounts" -Exe $ServerSetup -ArgumentList @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/ACCOUNTS=$badAccounts",
    "/LOG=`"$logPath`""
) -LogPath $logPath -ExpectExitCode $script:CheckpointSetupExitInvalidAccounts

if ($result.ExitCode -ne $script:CheckpointSetupExitInvalidAccounts) {
    throw "R8f-invalid-accounts: expected exit $($script:CheckpointSetupExitInvalidAccounts); got $($result.ExitCode)"
}
$logRaw = Get-Content -LiteralPath $logPath -Raw
if ($logRaw -notmatch "CHECKPOINT_ACCOUNTS_INVALID" -and $logRaw -notmatch "CHECKPOINT_SETUP_FAILURE message=.*CHECKPOINT_ACCOUNTS_INVALID") {
    throw "R8f-invalid-accounts: log missing CHECKPOINT_ACCOUNTS_INVALID (direct or via failure record)"
}

Write-Host "R8f-invalid-accounts: ok"