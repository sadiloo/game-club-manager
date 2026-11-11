$ErrorActionPreference = "Continue"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$artifactDir = Join-Path $env:GITHUB_WORKSPACE "artifacts"
if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) {
    $artifactDir = Join-Path $repoRoot "artifacts"
}

Write-Host "=== CI R11 evidence dump (step: print kiosk integration evidence) ==="

$logDir = Join-Path $artifactDir "kiosk-windows-logs"
foreach ($name in @("server-out.log", "server-err.log", "ci-phase.log")) {
    $p = Join-Path $logDir $name
    if (Test-Path $p) {
        Write-Host "--- tail $name (last 200 lines) ---"
        Get-Content $p -Tail 200 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
    }
}

$marker = Join-Path $artifactDir "ci-timeout-marker.log"
if (Test-Path $marker) {
    Write-Host "--- ci-timeout-marker.log (full) ---"
    Get-Content $marker -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
}

foreach ($trxName in @("kiosk-windows-server.trx", "kiosk-windows-guard.trx")) {
    $trxPath = Join-Path $artifactDir $trxName
    if (Test-Path $trxPath) {
        Write-Host "--- TRX summary: $trxName ---"
        try {
            [xml]$trx = Get-Content $trxPath
            foreach ($unit in $trx.TestRun.Results.UnitTestResult) {
                Write-Host ("{0}  {1}" -f $unit.outcome, $unit.testName)
            }
        } catch { }
    }
}

if (Test-Path $artifactDir) {
    Get-ChildItem -Path $artifactDir -Recurse -Filter "Sequence*.xml" -File -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host ("--- VSTest hang sequence: {0} ---" -f $_.FullName)
        Get-Content $_.FullName -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
    }
}

$watchdog = Join-Path $artifactDir "watchdog.log"
if (Test-Path $watchdog) {
    Write-Host "--- watchdog.log (full) ---"
    Get-Content $watchdog -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
}

$evidenceRoot = Join-Path $artifactDir "evidence"
if (Test-Path $evidenceRoot) {
    Get-ChildItem -Path $evidenceRoot -Recurse -File | ForEach-Object {
        Write-Host ("--- evidence file: {0} ---" -f $_.FullName)
        Get-Content $_.FullName -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ }
    }
}

$screenshot = Join-Path $evidenceRoot "screenshot-lock.png"
if (Test-Path $screenshot) {
    try {
        $info = Get-Item $screenshot
        $hash = (Get-FileHash -Algorithm SHA256 -Path $screenshot).Hash
        Write-Host ("--- screenshot-lock.png path={0} size={1} sha256={2} ---" -f $info.FullName, $info.Length, $hash)
    } catch { }
}

Write-Host "=== end CI R11 evidence dump ==="
exit 0
