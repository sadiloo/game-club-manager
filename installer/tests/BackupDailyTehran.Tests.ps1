# B-3: 09:00 Asia/Tehran maps to explicit UTC StartBoundary (portable).
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Import-Module (Join-Path $root "installer/tools/CheckpointInstall.Common.psm1") -Force
$utc = Get-CheckpointDailyBackupStartBoundaryUtc
$tehran = Get-CheckpointIranTimeZone
$localNine = [DateTime]::SpecifyKind([DateTime]::Today.AddHours(9), [DateTimeKind]::Unspecified)
$expect = [System.TimeZoneInfo]::ConvertTimeToUtc($localNine, $tehran).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
if ($utc -ne $expect) {
    throw "Tehran 09:00 boundary expected $expect got $utc"
}
$reg = Get-Content (Join-Path $root "installer/tools/Register-CheckpointDailyBackup.ps1") -Raw
if ($reg -notmatch "Iran Standard Time") { throw "Register script must set Iran Standard Time" }
if ($reg -notmatch "StartWhenAvailable") { throw "Register script must set StartWhenAvailable" }
Write-Host "BackupDailyTehran: ok boundary=$utc"