param(
    [string] $LogsDir
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

Write-Host "Q5-interactive: open-sessions confirm helpers are wired (non-silent path)"
$scriptPath = "C:\Program Files\Checkpoint\Server\tools\Get-InstallerOpenSessionCount.ps1"
if (-not (Test-Path -LiteralPath $scriptPath)) {
    throw "Q5-interactive: Get-InstallerOpenSessionCount.ps1 missing (server must be installed)"
}

$outFile = Join-Path $env:TEMP "q5-open-sessions-probe.txt"
if (Test-Path $outFile) { Remove-Item $outFile -Force }
$proc = Start-Process -FilePath "powershell.exe" -ArgumentList @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $scriptPath
) -RedirectStandardOutput $outFile -Wait -PassThru -WindowStyle Hidden
if ($proc.ExitCode -ne 0) {
    throw "Q5-interactive: open-session probe exit $($proc.ExitCode)"
}
$line = (Get-Content -LiteralPath $outFile -ErrorAction Stop | Select-Object -First 1).Trim()
if ($line -notmatch '^COUNT=\d+$') {
    throw "Q5-interactive: expected COUNT=<n>; got $line"
}

$inc = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\..\server\ServerWizard.inc.iss") -Raw
if ($inc -notmatch "ConfirmUpgradeDespiteOpenSessions") {
    throw "Q5-interactive: ConfirmUpgradeDespiteOpenSessions missing from wizard"
}
if ($inc -notmatch "TaskDialogMsgBox") {
    throw "Q5-interactive: interactive confirm must use TaskDialogMsgBox"
}

Write-Host "Q5-interactive: ok countLine=$line"