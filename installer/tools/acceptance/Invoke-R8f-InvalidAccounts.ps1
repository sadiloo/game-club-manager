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
if (-not (Select-String -LiteralPath $logPath -Pattern "CHECKPOINT_ACCOUNTS_INVALID" -SimpleMatch)) {
    throw "R8f-invalid-accounts: log missing CHECKPOINT_ACCOUNTS_INVALID"
}

Write-Host "R8f-invalid-accounts: ok"