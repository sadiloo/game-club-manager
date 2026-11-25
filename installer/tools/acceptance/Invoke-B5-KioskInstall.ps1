param(
    [string] $KioskSetup,
    [string] $LogsDir
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

Write-Host "B-5: silent kiosk install"
$setupArgs = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART")
$maxPasses = 2
for ($pass = 1; $pass -le $maxPasses; $pass++) {
    $log = Join-Path $LogsDir "b5-kiosk-install-pass$pass.log"
    $argList = $setupArgs + @("/LOG=$log")
    $proc = Start-Process -FilePath $KioskSetup -ArgumentList $argList -Wait -PassThru
    if ($proc.ExitCode -notin 0, 3010) {
        throw "B-5: kiosk setup exit $($proc.ExitCode) pass $pass"
    }
    if ($proc.ExitCode -eq 3010) {
        Write-Host "B-5: pass $pass exit 3010 (reboot needed, /NORESTART)"
    }
    Assert-InnoSetupLogNoFailedRuns -LogPath $log
    $postinstallOk = $true
    try {
        Assert-KioskPostinstallCompleted -InnoLogPath $log
    } catch {
        $postinstallOk = $false
        if ($pass -eq $maxPasses) { throw }
        Write-Host "B-5: pass $pass postinstall incomplete; re-running installer (deferred-reboot flow)"
    }
    if ($postinstallOk) { break }
}

$reg = Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -ErrorAction Stop
if ($reg.DefaultPassword) {
    throw "B-5: plaintext DefaultPassword in registry"
}

foreach ($name in @("CHECKPOINT_SERVER_URL", "CHECKPOINT_ADMIN_PASSWORD")) {
    $val = [Environment]::GetEnvironmentVariable($name, "Machine")
    if ($val) { throw "B-5: machine env $name must not be set" }
}

Restart-Service CheckpointGuard -ErrorAction Stop
Write-Host "B-5: ok"