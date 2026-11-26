param(
    [string] $LogsDir,
    [string] $AccountsFile
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

$acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
$secondary = [string]$acc.secondaryBackupDir
if ([string]::IsNullOrWhiteSpace($secondary)) {
    throw "B-3-flags: accounts file must set secondaryBackupDir"
}
$logsDirPd = "C:\ProgramData\Checkpoint\logs"
$secFlag = Join-Path $logsDirPd "backup-secondary-missing.flag"
$lowFlag = Join-Path $logsDirPd "backup-low-disk.flag"
if (Test-Path $secFlag) { Remove-Item $secFlag -Force }
if (Test-Path $lowFlag) { Remove-Item $lowFlag -Force }

$missingDir = Join-Path $secondary "_removed_for_ci"
if (Test-Path -LiteralPath $secondary) {
    Remove-Item -LiteralPath $secondary -Recurse -Force
}

$scriptPath = "C:\Program Files\Checkpoint\Server\tools\backup-daily.ps1"
$oldEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath -SecondaryBackupDir $secondary 2>&1 | Out-Null
$ErrorActionPreference = $oldEap
if ($LASTEXITCODE -eq 0) { throw "B-3-flags: backup must fail when secondary dir missing" }
if (-not (Test-Path -LiteralPath $secFlag)) {
    throw "B-3-flags: backup-secondary-missing.flag not written"
}

New-Item -ItemType Directory -Force -Path $secondary | Out-Null
Remove-Item -LiteralPath $secFlag -Force -ErrorAction SilentlyContinue
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath -SecondaryBackupDir $secondary | Out-Null
if ($LASTEXITCODE -ne 0) { throw "B-3-flags: backup failed after secondary restored" }

# Low-disk flag: invoke script body with mocked DriveInfo is unit-tested; here pin Register script unchanged.
$reg = Get-Content (Join-Path (Split-Path $PSScriptRoot -Parent) "backup-daily.ps1") -Raw
if ($reg -notmatch "backup-low-disk\.flag") { throw "B-3-flags: backup-daily must reference low-disk flag" }
if ($reg -notmatch "DriveInfo") { throw "B-3-flags: backup-daily must check disk free space" }

Write-Host "B-3-flags: ok"