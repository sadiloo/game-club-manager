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

$globalOffset = 0
if ($env:WPF_STABILITY_GLOBAL_OFFSET -match '^\d+$') {
    $globalOffset = [int]$env:WPF_STABILITY_GLOBAL_OFFSET
}
$batchIndex = 1
if ($env:WPF_STABILITY_BATCH_INDEX -match '^\d+$') {
    $batchIndex = [int]$env:WPF_STABILITY_BATCH_INDEX
}

$redRunsFile = Join-Path $logDir "wpf-stability-red-runs.jsonl"
$evidenceScript = Join-Path $PSScriptRoot "ci-wpf-stability-failure-evidence.ps1"
$fakeRunner = $env:WPF_STABILITY_FAKE_RUNNER

if ([string]::IsNullOrWhiteSpace($fakeRunner)) {
    dotnet build tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj -c Release -f net10.0 --nologo
    dotnet build tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj -c Release -f net48 --nologo

    Write-Host "Resetting live database before WPF stability runs..."
    $resetExit = dotnet test tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj `
        -c Release -f net10.0 --no-build --nologo `
        --filter "Category=LiveDbResetHarness" `
        --blame-hang-timeout 3m 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host $resetExit
        throw "LiveDatabaseReset harness failed"
    }
}

$serverDll = Join-Path $repoRoot "src/GameClub.Server/bin/Release/net10.0/GameClub.Server.dll"
if (-not (Test-Path $serverDll) -and [string]::IsNullOrWhiteSpace($fakeRunner)) {
    dotnet build src/GameClub.Server/GameClub.Server.csproj -c Release --nologo
}
$serverLog = Join-Path $logDir "wpf-stability-server-out.log"
$serverErr = Join-Path $logDir "wpf-stability-server-err.log"
$serverProc = $null
$batchReds = [System.Collections.Generic.List[int]]::new()

function Stop-ServerTree([int]$processId) {
    & taskkill.exe /T /F /PID $processId 2>$null | Out-Null
    $global:LASTEXITCODE = 0
}

function Record-RedRun([int]$GlobalRun, [int]$BatchRun, [string]$Reason) {
    $batchReds.Add($GlobalRun) | Out-Null
    $record = @{
        run       = $GlobalRun
        batch     = $batchIndex
        batchRun  = $BatchRun
        login     = $env:WPF_STABILITY_LOGIN
        reason    = $Reason
        atUtc     = (Get-Date).ToUniversalTime().ToString("o")
    } | ConvertTo-Json -Compress
    Add-Content -Path $redRunsFile -Value $record
    Write-Host "WPF_STABILITY_RED global_run=$GlobalRun batch=$batchIndex batch_run=$BatchRun reason=$Reason login=$($env:WPF_STABILITY_LOGIN)"
    if (Test-Path $evidenceScript) {
        $env:WPF_STABILITY_RED_GLOBAL_RUN = "$GlobalRun"
        $env:WPF_STABILITY_RED_LOGIN = $env:WPF_STABILITY_LOGIN
        & $evidenceScript
        $global:LASTEXITCODE = 0
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($fakeRunner)) {
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
    }

    $wpfFilter = 'FullyQualifiedName~wpf_remaining_time_matches_funded_session_within_one_tick'
    for ($run = 1; $run -le $runs; $run++) {
        $globalRun = $globalOffset + $run
        Write-Host "WPF stability run $run/$runs (global $globalRun)"
        $env:WPF_STABILITY_LOGIN = "fund" + [guid]::NewGuid().ToString("N").Substring(0, 8)
        $wpfExit = 0
        if (-not [string]::IsNullOrWhiteSpace($fakeRunner)) {
            & pwsh -NoProfile -File $fakeRunner -GlobalRun $globalRun -BatchRun $run -LoginName $env:WPF_STABILITY_LOGIN
            $wpfExit = $LASTEXITCODE
        } else {
            $trxName = "wpf-stability-run-$globalRun.trx"
            $wpfArgs = @(
                'test', 'tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj',
                '-c', 'Release', '-f', 'net48', '--no-build', '--nologo',
                '--filter', $wpfFilter,
                '--results-directory', $logDir,
                '--logger', "trx;LogFileName=$trxName",
                '--blame-hang', '--blame-hang-timeout', '3m'
            )
            $wpfProc = Start-Process -FilePath 'dotnet' -ArgumentList $wpfArgs -PassThru -NoNewWindow -WorkingDirectory $repoRoot
            $wpfDone = $wpfProc.WaitForExit(240000)
            if (-not $wpfDone) {
                Stop-ServerTree -processId $wpfProc.Id
                Record-RedRun -GlobalRun $globalRun -BatchRun $run -Reason "timeout_240s"
                Write-Host "WPF_STABILITY_PROGRESS $run/$runs (red, continuing)"
                continue
            }
            $wpfExit = $wpfProc.ExitCode
        }
        if ($wpfExit -ne 0) {
            if ([string]::IsNullOrWhiteSpace($fakeRunner) -or $env:WPF_STABILITY_SKIP_RED_EVIDENCE -ne '1') {
                Record-RedRun -GlobalRun $globalRun -BatchRun $run -Reason "exit_$wpfExit"
            } else {
                $batchReds.Add($globalRun) | Out-Null
                $record = @{ run = $globalRun; batch = $batchIndex; batchRun = $run; login = $env:WPF_STABILITY_LOGIN; reason = "exit_$wpfExit"; atUtc = (Get-Date).ToUniversalTime().ToString("o") } | ConvertTo-Json -Compress
                Add-Content -Path $redRunsFile -Value $record
            }
            Write-Host "WPF_STABILITY_PROGRESS $run/$runs (red, continuing)"
            continue
        }
        Write-Host "WPF_STABILITY_PROGRESS $run/$runs"
    }
    Write-Host "WPF_STABILITY_BATCH_COMPLETED batch=$batchIndex reds_in_batch=$($batchReds.Count)"
} finally {
    if ($null -ne $serverProc -and -not $serverProc.HasExited) {
        Stop-ServerTree -processId $serverProc.Id
    }
    $global:LASTEXITCODE = 0
}

exit 0