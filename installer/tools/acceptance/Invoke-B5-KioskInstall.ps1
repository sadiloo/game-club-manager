param(
    [string] $KioskSetup,
    [string] $LogsDir
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null
$log = Join-Path $LogsDir "b5-kiosk-install.log"

Write-Host "B-5: silent kiosk install"
$proc = Start-Process -FilePath $KioskSetup -ArgumentList @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$log"
) -Wait -PassThru
# Exit 3010 allowed when restart needed
if ($proc.ExitCode -notin 0, 3010) {
    throw "B-5: kiosk setup exit $($proc.ExitCode)"
}
if ($proc.ExitCode -eq 3010) {
    Write-Host "B-5: exit 3010 (restart needed) with /NORESTART as expected"
}
Assert-InnoSetupLogNoFailedRuns -LogPath $log
Assert-KioskPostinstallCompleted -InnoLogPath $log

$reg = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -ErrorAction Stop
if ($reg.DefaultPassword) {
    throw "B-5: plaintext DefaultPassword in registry"
}

foreach ($name in @("CHECKPOINT_SERVER_URL", "CHECKPOINT_ADMIN_PASSWORD")) {
    $val = [Environment]::GetEnvironmentVariable($name, "Machine")
    if ($val) { throw "B-5: machine env $name must not be set" }
}

$fpMatch = Select-String -LiteralPath $log -Pattern "password fingerprint sha256=([0-9a-f]{64}) length=(\d+)" | Select-Object -Last 1
if (-not $fpMatch) { throw "B-5: kiosk password fingerprint line missing from Inno log" }
$len = [int]$fpMatch.Matches[0].Groups[2].Value
if ($len -lt 24) { throw "B-5: kiosk password length $len must be >= 24" }

$fp1 = $fpMatch.Matches[0].Groups[1].Value
$kioskUnins = Get-ChildItem "C:\Program Files\Checkpoint\Kiosk\unins*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($kioskUnins) {
    $u = Start-Process -FilePath $kioskUnins.FullName -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES") -Wait -PassThru
    if ($u.ExitCode -ne 0) { throw "B-5: kiosk uninstall exit $($u.ExitCode)" }
}
$log2 = Join-Path $LogsDir "b5-kiosk-install-second.log"
$proc2 = Start-Process -FilePath $KioskSetup -ArgumentList @(
    "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$log2"
) -Wait -PassThru
if ($proc2.ExitCode -notin 0, 3010) { throw "B-5: second install exit $($proc2.ExitCode)" }
$fpLine2 = Select-String -LiteralPath $log2 -Pattern "password fingerprint sha256=([0-9a-f]{64})" | Select-Object -Last 1
if (-not $fpLine2) { throw "B-5: second install fingerprint line missing" }
$fp2 = $fpLine2.Matches[0].Groups[1].Value
if ($fp1 -eq $fp2) { throw "B-5: kiosk password fingerprint must differ between installs" }

Start-Sleep -Seconds 5
Start-InstallerCheckpointGuard
Write-Host "B-5: ok length=$len"