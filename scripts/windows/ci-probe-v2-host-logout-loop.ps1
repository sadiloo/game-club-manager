$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

$artifactDir = Join-Path $env:GITHUB_WORKSPACE "artifacts"
if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) {
    $artifactDir = Join-Path $repoRoot "artifacts"
}
$logDir = Join-Path $artifactDir "kiosk-windows-logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$runs = 20
if ($env:HOST_LOGOUT_LOOP_RUNS -match '^\d+$') { $runs = [int]$env:HOST_LOGOUT_LOOP_RUNS }
$failLate = $env:HOST_LOGOUT_FAIL_LATE -eq '1'
$summaryPath = Join-Path $logDir "host-logout-open-ack-summary.log"

dotnet build tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj -c Release -f net48 --nologo

$filter = 'FullyQualifiedName~host_logout_open_ack_during_slow_post_stays_locked_without_session'
$failureCount = 0
$exitCode = 0

function Write-HostLogoutSummaryLine([string]$line) {
    Add-Content -Path $summaryPath -Value $line -Encoding UTF8
    Write-Host $line
}

function Write-HostLogoutFailureEvidence([int]$RunNumber, [string]$RunLog, [string]$RunErr) {
    $evidencePath = Join-Path $logDir ("host-logout-failure-evidence-" + $RunNumber + ".log")
    $utc = [datetime]::UtcNow.ToString("o")
    $lines = New-Object System.Collections.Generic.List[string]
    $append = { param($s) $lines.Add($s); Write-Host $s }
    & $append "PROBE_V2_HOST_LOGOUT_FAILURE utc=$utc run=$RunNumber"
    foreach ($path in @($RunLog, $RunErr)) {
        if (-not (Test-Path $path)) { continue }
        & $append "--- $($path) ---"
        Get-Content $path | ForEach-Object { & $append $_ }
    }
    $lines | Set-Content -Path $evidencePath -Encoding UTF8
}

for ($run = 1; $run -le $runs; $run++) {
    $stabMs = [long]([datetimeoffset]::UtcNow.ToUnixTimeMilliseconds())
    Write-Host "STAB_STEP utc_ms=$stabMs step=host_logout_run_begin run=$run/$runs"
    $runLog = Join-Path $logDir ("host-logout-open-ack-run-" + $run + ".log")
    $runErr = Join-Path $logDir ("host-logout-open-ack-run-" + $run + "-err.log")
    if (Test-Path $runLog) { Remove-Item -Force $runLog }
    if (Test-Path $runErr) { Remove-Item -Force $runErr }
    $args = @(
        'test', 'tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj',
        '-c', 'Release', '-f', 'net48', '--no-build', '--nologo',
        '--filter', $filter,
        '--blame-hang', '--blame-hang-timeout', '3m'
    )
    $proc = Start-Process -FilePath 'dotnet' -ArgumentList $args -PassThru -NoNewWindow -WorkingDirectory $repoRoot `
        -RedirectStandardOutput $runLog -RedirectStandardError $runErr
    $done = $proc.WaitForExit(180000)
    if (-not $done) {
        $failureCount++
        Write-HostLogoutSummaryLine "HOST_LOGOUT_FAIL run=$run reason=timeout"
        Write-HostLogoutFailureEvidence -RunNumber $run -RunLog $runLog -RunErr $runErr
        if (-not $failLate) { throw "host logout loop run $run timed out" }
        continue
    }
    if ($proc.ExitCode -ne 0) {
        $failureCount++
        Write-HostLogoutSummaryLine "HOST_LOGOUT_FAIL run=$run exit=$($proc.ExitCode)"
        Write-HostLogoutFailureEvidence -RunNumber $run -RunLog $runLog -RunErr $runErr
        if (-not $failLate) { throw "host logout loop run $run failed exit $($proc.ExitCode)" }
        continue
    }
    $endMs = [long]([datetimeoffset]::UtcNow.ToUnixTimeMilliseconds())
    Write-Host "STAB_STEP utc_ms=$endMs step=host_logout_run_end run=$run/$runs exit=0"
    Write-HostLogoutSummaryLine "HOST_LOGOUT_PASS run=$run"
}

Write-Host "HOST_LOGOUT_LOOP_COMPLETED runs=$runs failures=$failureCount"
if ($failLate -and $failureCount -gt 0) {
    Write-HostLogoutSummaryLine "HOST_LOGOUT_LOOP_FAIL count=$failureCount"
    exit 1
}
exit 0