# Register CheckpointDailyBackup at 09:00 Asia/Tehran (B-3).
param(
    [string] $BackupScriptPath,
    [string] $SecondaryBackupDir = ""
)

$ErrorActionPreference = "Stop"
$taskName = "CheckpointDailyBackup"
$argList = "-NoProfile -ExecutionPolicy Bypass -File `"$BackupScriptPath`""
if (-not [string]::IsNullOrWhiteSpace($SecondaryBackupDir)) {
    $argList += " -SecondaryBackupDir `"$SecondaryBackupDir`""
}
Import-Module (Join-Path $PSScriptRoot "CheckpointInstall.Common.psm1") -Force
$startBoundary = Get-CheckpointDailyBackupStartBoundaryUtc

$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><URI>\$taskName</URI></RegistrationInfo>
  <Triggers>
    <CalendarTrigger>
      <StartBoundary>$startBoundary</StartBoundary>
      <ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>
    </CalendarTrigger>
  </Triggers>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>true</Enabled>
  </Settings>
  <Actions>
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>$argList</Arguments>
    </Exec>
  </Actions>
</Task>
"@
$xmlPath = Join-Path $env:TEMP "checkpoint-daily-backup-task.xml"
Set-Content -LiteralPath $xmlPath -Value $xml -Encoding Unicode
$oldEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    $null = & schtasks.exe /Create /TN $taskName /XML $xmlPath /F 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "schtasks /Create failed for $taskName (exit $LASTEXITCODE)"
    }
    $null = & schtasks.exe /Change /TN $taskName /TZ "Iran Standard Time" 2>&1
} finally {
    $ErrorActionPreference = $oldEap
}
Write-Host "Register-CheckpointDailyBackup: task=$taskName startUtc=$startBoundary tehran=09:00"