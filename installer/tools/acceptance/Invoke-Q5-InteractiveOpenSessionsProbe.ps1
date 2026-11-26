param(
    [string] $LogsDir,
    [string] $AccountsFile = (Join-Path $PSScriptRoot "..\..\ci\accounts-silent.json")
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
New-Item -ItemType Directory -Force -Path $LogsDir | Out-Null

Write-Host "Q5-interactive: open-session count must match seeded sessions"
$scriptPath = "C:\Program Files\Checkpoint\Server\tools\Get-InstallerOpenSessionCount.ps1"
if (-not (Test-Path -LiteralPath $scriptPath)) {
    throw "Q5-interactive: Get-InstallerOpenSessionCount.ps1 missing (server must be installed)"
}

& (Join-Path $PSScriptRoot "Seed-InstallerAcceptanceData.ps1") -AccountsFile $AccountsFile -WithOpenSessions
$expectedCount = 2

$outFile = Join-Path $env:TEMP "q5-open-sessions-probe.txt"
if (Test-Path $outFile) { Remove-Item $outFile -Force }
# R10m DEF-28: the path has a space ("Program Files"); Invoke-InstallerOpenSessionProbe passes it as one quoted -File argument.
$probeExit = Invoke-InstallerOpenSessionProbe -ScriptPath $scriptPath -OutFile $outFile
if ($probeExit -ne 0) {
    $probeOut = if (Test-Path -LiteralPath $outFile) { (Get-Content -LiteralPath $outFile -Raw) } else { "<no output>" }
    throw "Q5-interactive: open-session probe exit $probeExit output=$probeOut"
}
$line = (Get-Content -LiteralPath $outFile -ErrorAction Stop | Select-Object -First 1).Trim()
if ($line -notmatch '^COUNT=(\d+)$') {
    throw "Q5-interactive: expected COUNT=<n>; got $line"
}
$count = [int]$Matches[1]
if ($count -ne $expectedCount) {
    throw "Q5-interactive: expected COUNT=$expectedCount after seed; got $count"
}

$inc = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\..\server\ServerWizard.inc.iss") -Raw
if ($inc -notmatch "ConfirmUpgradeDespiteOpenSessions") {
    throw "Q5-interactive: ConfirmUpgradeDespiteOpenSessions missing from wizard"
}
if ($inc -notmatch "TaskDialogMsgBox") {
    throw "Q5-interactive: interactive confirm must use TaskDialogMsgBox"
}

Write-Host "Q5-interactive: ok count=$count"