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

$serverDll = Join-Path $repoRoot "tests/GameClub.Server.TestHost/bin/Release/net10.0/GameClub.Server.TestHost.dll"
if (-not (Test-Path $serverDll)) {
    dotnet build tests/GameClub.Server.TestHost/GameClub.Server.TestHost.csproj -c Release --nologo
}
$script:WpfStabilityServerBandArgs = @('--pc-band', 'day')
$env:GAMECLUB_SERVER_HOST_ARGS = ($script:WpfStabilityServerBandArgs -join ' ')
$serverLog = Join-Path $logDir "wpf-stability-server-out.log"
$serverErr = Join-Path $logDir "wpf-stability-server-err.log"
$serverProc = $null
$exitCode = 0

function Stop-ServerTree([int]$processId) {
    & taskkill.exe /T /F /PID $processId 2>$null | Out-Null
    $global:LASTEXITCODE = 0
}

function Test-TcpPortTaken([int]$Port) {
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $client.Connect('127.0.0.1', $Port)
        $client.Close()
        return $true
    } catch {
        return $false
    }
}

function Wait-CiServerPortFree([int]$Port, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-TcpPortTaken -Port $Port)) { return }
        Start-Sleep -Seconds 1
    }
    throw "TCP port 127.0.0.1:${Port} still in use after ${TimeoutSec}s before WPF stability server start."
}

function Stop-OrphanedGameClubTestHosts() {
    foreach ($p in Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -match 'GameClub\.Server\.TestHost' }) {
        Stop-ServerTree -processId $p.ProcessId
    }
    $global:LASTEXITCODE = 0
}

try {
    Stop-OrphanedGameClubTestHosts
    $uri = [Uri]$env:CHECKPOINT_SERVER_URL
    $port = if ($uri.Port -gt 0) { $uri.Port } else { 80 }
    Wait-CiServerPortFree -Port $port -TimeoutSec 45
    if (Test-TcpPortTaken -Port $port) { throw "Port $port still taken before WPF stability" }
    if (Test-Path $serverLog) { Remove-Item -Force $serverLog }
    if (Test-Path $serverErr) { Remove-Item -Force $serverErr }

    Write-Host "live_server_wpf_stability pc_band=day start=(harness) end=(harness)"
    $serverProc = Start-Process -FilePath "dotnet" -ArgumentList (@("exec", $serverDll) + $script:WpfStabilityServerBandArgs) -PassThru -NoNewWindow `
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
    if (-not $ready) { throw "GameClub.Server.TestHost not ready for WPF stability" }
  Start-Sleep -Seconds 1
    $bandLines = @(Select-String -Path $serverLog -Pattern '^pc_band=' -ErrorAction SilentlyContinue)
    if ($bandLines.Count -eq 0) {
        throw "WPF stability server logged zero pc_band lines"
    }
    $production = @($bandLines | Where-Object { $_.Line -match '^pc_band=production\b' })
    if ($production.Count -gt 0) {
        throw "WPF stability server logged pc_band=production"
    }
    foreach ($bandLine in $bandLines) { Write-Host $bandLine.Line }

    $wpfFilter = 'FullyQualifiedName~wpf_remaining_time_matches_funded_session_within_one_tick'
    for ($run = 1; $run -le $runs; $run++) {
        Write-Host "WPF stability run $run/$runs"
        $env:WPF_STABILITY_LOGIN = "fund" + [guid]::NewGuid().ToString("N").Substring(0, 8)
        $wpfArgs = @(
            'test', 'tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj',
            '-c', 'Release', '-f', 'net48', '--no-build', '--nologo',
            '--filter', $wpfFilter,
            '--blame-hang', '--blame-hang-timeout', '3m'
        )
        $wpfProc = Start-Process -FilePath 'dotnet' -ArgumentList $wpfArgs -PassThru -NoNewWindow -WorkingDirectory $repoRoot
        $wpfDone = $wpfProc.WaitForExit(240000)
        if (-not $wpfDone) {
            Stop-ServerTree -processId $wpfProc.Id
            throw "WPF stability run $run timed out after 240s"
        }
        if ($wpfProc.ExitCode -ne 0) {
            throw "WPF stability run $run failed with exit $($wpfProc.ExitCode); see $serverErr"
        }
        Write-Host "WPF_STABILITY_PROGRESS $run/$runs"
    }
    Write-Host "WPF_STABILITY_COMPLETED $runs/$runs"
} catch {
  Write-Host "ci-integration-wpf-stability.ps1 error: $($_.Exception.Message)"
  $exitCode = 1
} finally {
    if ($null -ne $serverProc -and -not $serverProc.HasExited) {
        Stop-ServerTree -processId $serverProc.Id
    }
    Stop-OrphanedGameClubTestHosts
    $global:LASTEXITCODE = 0
}

if ($exitCode -ne 0) { exit $exitCode }
Write-Host "ci-integration-wpf-stability.ps1 completed $runs/$runs runs."
exit 0