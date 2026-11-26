param(
    [string] $KioskSetupHooks,
    [string] $LogsDir
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

Write-Host "R8f-kiosk: postinstall fail hook"
$logPath = Join-Path $LogsDir "acceptance-setup-r8f-kiosk-postinstall-fail.log"
if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }
$result = Start-Process -FilePath $KioskSetupHooks -ArgumentList @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/KIOSKPOSTINSTALLFAIL=1", "/LOG=`"$logPath`""
) -Wait -PassThru
if ($result.ExitCode -ne $script:CheckpointSetupExitFailKioskPostinstall) {
    throw "R8f-kiosk: expected exit $($script:CheckpointSetupExitFailKioskPostinstall); got $($result.ExitCode)"
}
if (-not (Select-String -LiteralPath $logPath -Pattern "CHECKPOINT_KIOSK_POSTINSTALL_ABORT" -SimpleMatch)) {
    throw "R8f-kiosk: Inno log missing CHECKPOINT_KIOSK_POSTINSTALL_ABORT"
}
Write-Host "R8f-kiosk: ok"