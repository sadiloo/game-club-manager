$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

if (-not $env:GAMECLUB_TEST_DATABASE) {
    $env:GAMECLUB_TEST_DATABASE = "Host=localhost;Port=5432;Database=gameclub_test;Username=postgres;Password=postgres;SSL Mode=Disable"
    $env:GAMECLUB_DATABASE = $env:GAMECLUB_TEST_DATABASE
}
if (-not $env:GAMECLUB_API_KEY) { $env:GAMECLUB_API_KEY = "ci-test-key" }
if (-not $env:CHECKPOINT_SERVER_URL) { $env:CHECKPOINT_SERVER_URL = "http://127.0.0.1:5225" }
if (-not $env:OperatorSessionSecret) { $env:OperatorSessionSecret = "ci-operator-secret-r11" }
$env:ASPNETCORE_URLS = $env:CHECKPOINT_SERVER_URL
$env:ASPNETCORE_ENVIRONMENT = "Production"

$artifactDir = Join-Path $env:GITHUB_WORKSPACE "artifacts"
if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) {
    $artifactDir = Join-Path $repoRoot "artifacts"
}
$logDir = Join-Path $artifactDir "kiosk-windows-logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$runs = 20
if ($env:WPF_STABILITY_RUNS -match '^\d+$') {
    $runs = [int]$env:WPF_STABILITY_RUNS
}
if ($runs -le 0) {
    Write-Host "WPF stability runs disabled (WPF_STABILITY_RUNS=$runs)"
    exit 0
}

$batchIndex = 1
$batchOffset = 0
if ($env:WPF_STABILITY_BATCH -match '^\d+$') { $batchIndex = [int]$env:WPF_STABILITY_BATCH }
if ($env:WPF_STABILITY_BATCH_OFFSET -match '^\d+$') { $batchOffset = [int]$env:WPF_STABILITY_BATCH_OFFSET }
$failLate = $env:WPF_STABILITY_FAIL_LATE -eq '1'
$probeV2 = $env:WPF_STABILITY_PROBE_V2 -eq '1'
$probeV3 = $env:WPF_STABILITY_PROBE_V3 -eq '1'
$probeEvidence = $probeV2 -or $probeV3

dotnet build tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj -c Release -f net10.0 --nologo
dotnet build tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj -c Release -f net48 --nologo
dotnet build src/GameClub.Kiosk/GameClub.Kiosk.csproj -c Release -f net48 --nologo

Write-Host "Resetting live database before WPF stability runs (batch=$batchIndex)..."
$resetExit = dotnet test tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj `
    -c Release -f net10.0 --no-build --nologo `
    --filter "Category=LiveDbResetHarness" `
    --blame-hang-timeout 3m 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host $resetExit
    throw "LiveDatabaseReset harness failed"
}

$serverDll = Join-Path $repoRoot "src/GameClub.Server/bin/Release/net10.0/GameClub.Server.dll"
if (-not (Test-Path $serverDll)) {
    dotnet build src/GameClub.Server/GameClub.Server.csproj -c Release --nologo
}
$serverLog = Join-Path $logDir "wpf-stability-server-out.log"
$serverErr = Join-Path $logDir "wpf-stability-server-err.log"
if ($batchIndex -gt 1) {
    $serverLog = Join-Path $logDir ("wpf-stability-server-out-batch" + $batchIndex + ".log")
    $serverErr = Join-Path $logDir ("wpf-stability-server-err-batch" + $batchIndex + ".log")
}
$serverProc = $null
$exitCode = 0
$failureCount = 0
$summaryPath = Join-Path $logDir "wpf-stability-summary.log"

function Stop-ServerTree([int]$processId) {
    & taskkill.exe /T /F /PID $processId 2>$null | Out-Null
    $global:LASTEXITCODE = 0
}

function Copy-KioskDiagnosticLogs([int]$RunNumber) {
    $kioskLogDir = Join-Path $env:ProgramData "Checkpoint\kiosk"
    if (Test-Path $kioskLogDir) {
        Get-ChildItem -Path $kioskLogDir -Filter "*.log" -ErrorAction SilentlyContinue | ForEach-Object {
            Copy-Item $_.FullName (Join-Path $logDir ("wpf-stability-" + $RunNumber + "-" + $_.Name)) -Force -ErrorAction SilentlyContinue
        }
    }
}

function Write-StabilitySummaryLine([string]$line) {
    Add-Content -Path $summaryPath -Value $line -Encoding UTF8
    Write-Host $line
}

function Get-StationIdFromLogs([string[]]$Paths) {
    foreach ($path in $Paths) {
        if (-not (Test-Path $path)) { continue }
        $text = Get-Content $path -Raw -ErrorAction SilentlyContinue
        if ($text -match 'rawStationId=(PC-WPF-[0-9a-f]{8})') { return $Matches[1] }
        if ($text -match 'stationId=(PC-WPF-[0-9a-f]{8})') { return $Matches[1] }
        if ($text -match '(PC-WPF-[0-9a-f]{8})') { return $Matches[1] }
    }
    return $null
}

function Invoke-ProbePostgresQuery([string]$Query, [string]$Label, [scriptblock]$Append) {
    $psql = 'C:\Program Files\PostgreSQL\16\bin\psql.exe'
    if (-not (Test-Path $psql)) {
        & $Append "($($Label): psql missing)"
        return
    }
    $conn = $env:GAMECLUB_TEST_DATABASE
    if ($conn -match 'Password=([^;]+)') { $env:PGPASSWORD = $Matches[1] }
    $db = if ($conn -match 'Database=([^;]+)') { $Matches[1] } else { 'gameclub_test' }
    $hostName = if ($conn -match 'Host=([^;]+)') { $Matches[1] } else { '127.0.0.1' }
    $port = if ($conn -match 'Port=([^;]+)') { $Matches[1] } else { '5432' }
    $user = if ($conn -match 'Username=([^;]+)') { $Matches[1] } else { 'postgres' }
    & $Append "--- $Label ---"
    $pgOut = & $psql -h $hostName -p $port -U $user -d $db -c $Query 2>&1
    ($pgOut | Out-String).Trim().Split("`n") | ForEach-Object { if ($_.Length -gt 0) { & $Append $_ } }
}

function Write-ProbeFailureEvidence(
    [int]$GlobalRun,
    [string]$ServerOutLog,
    [string]$ServerErrLog,
    [string]$RunLog,
    [string]$RunErr
) {
    $evidencePath = Join-Path $logDir ("wpf-stability-failure-evidence-" + $GlobalRun + ".log")
    $utc = [datetime]::UtcNow.ToString("o")
    $lines = New-Object System.Collections.Generic.List[string]
    $append = { param($s) $lines.Add($s); Write-Host $s }
    $tag = if ($probeV3) { "PROBE_V3_FAILURE_EVIDENCE" } else { "PROBE_V2_FAILURE_EVIDENCE" }
    & $append "$tag utc=$utc global_run=$GlobalRun"

    & $append "--- HB_ACK and lease lines (run log) ---"
    if (Test-Path $RunLog) {
        Select-String -Path $RunLog -Pattern 'HB_ACK|lease|lockedReason|STAB_STEP' -ErrorAction SilentlyContinue |
            ForEach-Object { & $append $_.Line }
    }
    if (Test-Path $RunErr) {
        Select-String -Path $RunErr -Pattern 'HB_ACK|lease|lockedReason|STAB_STEP' -ErrorAction SilentlyContinue |
            ForEach-Object { & $append $_.Line }
    }

    & $append "--- full server stdout for this batch ---"
    if (Test-Path $ServerOutLog) { Get-Content $ServerOutLog | ForEach-Object { & $append $_ } }
    & $append "--- full server stderr for this batch ---"
    if (Test-Path $ServerErrLog) { Get-Content $ServerErrLog | ForEach-Object { & $append $_ } }

    if ($probeV3) {
        & $append "--- (1) ordered /api/kiosk and /api/operator calls (server PROBE_V3_API + test PROBE_HTTP_TRACE) ---"
        foreach ($path in @($ServerOutLog, $ServerErrLog, $RunLog, $RunErr)) {
            if (-not (Test-Path $path)) { continue }
            Select-String -Path $path -Pattern '^PROBE_V3_API |^PROBE_HTTP_TRACE ' -ErrorAction SilentlyContinue |
                ForEach-Object { & $append ("source=$([IO.Path]::GetFileName($path)) " + $_.Line) }
        }
        & $append "--- (2) server log fund window: PROBE_V3_SESSION + exceptions (stderr) ---"
        foreach ($path in @($ServerOutLog, $ServerErrLog)) {
            if (-not (Test-Path $path)) { continue }
            Select-String -Path $path -Pattern 'PROBE_V3_SESSION|Exception|session_elsewhere|station_busy|no_credit|invalid_login|rejected' -ErrorAction SilentlyContinue |
                ForEach-Object { & $append ("source=$([IO.Path]::GetFileName($path)) " + $_.Line) }
        }
        & $append "--- (3) test step trace (STAB_STEP; harness does not call session start) ---"
        foreach ($path in @($RunLog, $RunErr)) {
            if (-not (Test-Path $path)) { continue }
            Select-String -Path $path -Pattern '^STAB_STEP ' -ErrorAction SilentlyContinue |
                ForEach-Object { & $append $_.Line }
        }
    } else {
        & $append "--- /api/kiosk lines (server logs + run output) ---"
        $kioskPatterns = @('/api/kiosk', 'POST /api/kiosk', 'GET /api/kiosk', 'kiosk.login', 'kiosk\.')
        foreach ($path in @($ServerOutLog, $ServerErrLog, $RunLog, $RunErr)) {
            if (-not (Test-Path $path)) { continue }
            Select-String -Path $path -Pattern ($kioskPatterns -join '|') -ErrorAction SilentlyContinue |
                ForEach-Object { & $append ("source=$([IO.Path]::GetFileName($path)) " + $_.Line) }
        }
        Get-ChildItem $logDir -Filter ("wpf-stability-" + $GlobalRun + "-*.log") -ErrorAction SilentlyContinue | ForEach-Object {
            Select-String -Path $_.FullName -Pattern '/api/kiosk|HB_ACK|HTTP' -ErrorAction SilentlyContinue |
                ForEach-Object { & $append ("source=$($_.Filename) " + $_.Line) }
        }
    }

    $stationId = Get-StationIdFromLogs @($RunLog, $RunErr, $ServerOutLog, $ServerErrLog)
  $stationIdUpper = if ($stationId) { $stationId.ToUpperInvariant() } else { $null }

    & $append "--- (4) postgres stations row + all sessions (raw vs normalized station id) ---"
    if ($stationId) {
        & $append "rawStationIdFromTest=$stationId"
        & $append "serverNormalizedStationId=$stationIdUpper"
        Invoke-ProbePostgresQuery -Label "stations row (raw id)" -Query @"
SELECT "Id", "Name", "GroupId"
FROM stations
WHERE "Id" = '$stationId';
"@ -Append $append
        if ($stationIdUpper -ne $stationId) {
            Invoke-ProbePostgresQuery -Label "stations row (normalized id)" -Query @"
SELECT "Id", "Name", "GroupId"
FROM stations
WHERE "Id" = '$stationIdUpper';
"@ -Append $append
        }
        Invoke-ProbePostgresQuery -Label "sessions for raw station id" -Query @"
SELECT "Id", "StationId", "Status", "StartedAtUtc", "EndedAtUtc", "CustomerId"
FROM sessions
WHERE "StationId" = '$stationId'
ORDER BY "StartedAtUtc" DESC
LIMIT 50;
"@ -Append $append
        if ($stationIdUpper -ne $stationId) {
            Invoke-ProbePostgresQuery -Label "sessions for normalized station id" -Query @"
SELECT "Id", "StationId", "Status", "StartedAtUtc", "EndedAtUtc", "CustomerId"
FROM sessions
WHERE "StationId" = '$stationIdUpper'
ORDER BY "StartedAtUtc" DESC
LIMIT 50;
"@ -Append $append
        }
    } else {
        & $append "(station id not found in logs or psql missing)"
    }

    $lines | Set-Content -Path $evidencePath -Encoding UTF8
    & $append "--- evidence written to $evidencePath ---"
}

try {
    if ($probeV3) { $env:GAMECLUB_PROBE_HTTP_LOG = '1' }
    $serverProc = Start-Process -FilePath "dotnet" -ArgumentList @("exec", $serverDll) -PassThru -NoNewWindow `
        -RedirectStandardOutput $serverLog -RedirectStandardError $serverErr -WorkingDirectory $repoRoot
    $env:GAMECLUB_SERVER_PID = $serverProc.Id
    $env:GAMECLUB_SERVER_DLL = $serverDll

    $ready = $false
    $deadline = (Get-Date).AddMinutes(2)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest -Uri "$($env:CHECKPOINT_SERVER_URL)/api/stations" -Headers @{ "X-Api-Key" = $env:GAMECLUB_API_KEY } -UseBasicParsing -TimeoutSec 3
            if ($r.StatusCode -eq 200) { $ready = $true; break }
        } catch {
            Start-Sleep -Seconds 2
        }
    }
    if (-not $ready) { throw "GameClub.Server not ready for WPF stability" }

    $wpfFilter = 'FullyQualifiedName~wpf_remaining_time_matches_funded_session_within_one_tick'
    for ($run = 1; $run -le $runs; $run++) {
        $globalRun = $batchOffset + $run
        $stabMs = [long]([datetimeoffset]::UtcNow.ToUnixTimeMilliseconds())
        Write-Host "STAB_STEP utc_ms=$stabMs step=run_begin batch=$batchIndex run=$run/$runs global=$globalRun"
        Write-Host "WPF stability run $run/$runs (global $globalRun)"
        $env:WPF_STABILITY_LOGIN = "fund" + [guid]::NewGuid().ToString("N").Substring(0, 8)
        $runLog = Join-Path $logDir ("wpf-stability-run-" + $globalRun + ".log")
        $runErr = Join-Path $logDir ("wpf-stability-run-" + $globalRun + "-err.log")
        if (Test-Path $runLog) { Remove-Item -Force $runLog }
        if (Test-Path $runErr) { Remove-Item -Force $runErr }
        $wpfArgs = @(
            'test', 'tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj',
            '-c', 'Release', '-f', 'net48', '--no-build', '--nologo',
            '--filter', $wpfFilter,
            '--blame-hang', '--blame-hang-timeout', '3m'
        )
        $wpfProc = Start-Process -FilePath 'dotnet' -ArgumentList $wpfArgs -PassThru -NoNewWindow -WorkingDirectory $repoRoot `
            -RedirectStandardOutput $runLog -RedirectStandardError $runErr
        $wpfDone = $wpfProc.WaitForExit(240000)
        if (-not $wpfDone) {
            Stop-ServerTree -processId $wpfProc.Id
            $failureCount++
            Write-StabilitySummaryLine "WPF_STABILITY_FAIL global=$globalRun reason=timeout"
            Copy-KioskDiagnosticLogs -RunNumber $globalRun
            if ($probeEvidence) { Write-ProbeFailureEvidence -GlobalRun $globalRun -ServerOutLog $serverLog -ServerErrLog $serverErr -RunLog $runLog -RunErr $runErr }
            if (-not $failLate) { throw "WPF stability run $globalRun timed out after 240s" }
            continue
        }
        Copy-KioskDiagnosticLogs -RunNumber $globalRun
        if ($wpfProc.ExitCode -ne 0) {
            $failureCount++
            Write-StabilitySummaryLine "WPF_STABILITY_FAIL global=$globalRun exit=$($wpfProc.ExitCode) log=$runLog"
            if ($probeEvidence) { Write-ProbeFailureEvidence -GlobalRun $globalRun -ServerOutLog $serverLog -ServerErrLog $serverErr -RunLog $runLog -RunErr $runErr }
            if (-not $failLate) {
                throw "WPF stability run $globalRun failed with exit $($wpfProc.ExitCode); see $serverErr and $runLog"
            }
            continue
        }
        $stabEndMs = [long]([datetimeoffset]::UtcNow.ToUnixTimeMilliseconds())
        Write-Host "STAB_STEP utc_ms=$stabEndMs step=run_end batch=$batchIndex run=$run/$runs global=$globalRun exit=0"
        Write-StabilitySummaryLine "WPF_STABILITY_PASS global=$globalRun"
        Write-Host "WPF_STABILITY_PROGRESS $globalRun"
    }
    Write-Host "WPF_STABILITY_BATCH_COMPLETED batch=$batchIndex runs=$runs failures=$failureCount"
    if ($failLate -and $failureCount -gt 0) {
        Write-StabilitySummaryLine "WPF_STABILITY_BATCH_FAIL batch=$batchIndex count=$failureCount"
        $exitCode = 1
    }
} catch {
    Write-Host "ci-integration-wpf-stability.ps1 error: $($_.Exception.Message)"
    $exitCode = 1
} finally {
    if ($null -ne $serverProc -and -not $serverProc.HasExited) {
        Stop-ServerTree -processId $serverProc.Id
    }
    $global:LASTEXITCODE = 0
}

if ($exitCode -ne 0) { exit $exitCode }
Write-Host "ci-integration-wpf-stability.ps1 completed batch=$batchIndex $runs/$runs runs."
exit 0