$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

$artifactDir = Join-Path $env:GITHUB_WORKSPACE "artifacts"
if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) {
    $artifactDir = Join-Path $repoRoot "artifacts"
}
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

if (-not $env:GAMECLUB_TEST_DATABASE) {
    $env:GAMECLUB_TEST_DATABASE = "Host=localhost;Port=5432;Database=gameclub_test;Username=postgres;Password=postgres"
    $env:GAMECLUB_DATABASE = $env:GAMECLUB_TEST_DATABASE
    $env:GAMECLUB_API_KEY = "ci-test-key"
}

$kioskUser = $env:CHECKPOINT_KIOSK_USER
if ([string]::IsNullOrWhiteSpace($kioskUser)) { $kioskUser = "CheckpointKiosk" }
$sid = $env:CHECKPOINT_KIOSK_SID
if ([string]::IsNullOrWhiteSpace($sid)) {
    try {
        $sid = (New-Object System.Security.Principal.NTAccount($env:USERNAME)).Translate([System.Security.Principal.SecurityIdentifier]).Value
    } catch {
        $sid = (New-Object System.Security.Principal.NTAccount($kioskUser)).Translate([System.Security.Principal.SecurityIdentifier]).Value
    }
}
$env:CHECKPOINT_KIOSK_SID = $sid
$env:CHECKPOINT_KIOSK_USER = $kioskUser

Write-Host "Building Release (-warnaserror)..."
dotnet build GameClub.slnx -c Release -warnaserror --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$logDir = Join-Path $artifactDir "kiosk-windows-logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# Server + KioskRuntime hub tests (net10) run on ubuntu net10 with the Linux postgres service.
# This job executes real Windows-only guard registry/ACL and WPF smoke (net48).
Write-Host "Running windows guard/wpf integration (net48)..."
dotnet test tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj `
    -c Release -f net48 --no-build --nologo `
    --logger "trx;LogFileName=kiosk-windows-guard.trx" `
    --results-directory $artifactDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$watchdog = Join-Path $env:ProgramData "Checkpoint\guard\watchdog.log"
if (Test-Path $watchdog) {
    Copy-Item $watchdog (Join-Path $artifactDir "watchdog.log") -Force
}

$evidence = Join-Path $env:ProgramData "Checkpoint\evidence"
if (Test-Path $evidence) {
    Copy-Item $evidence (Join-Path $artifactDir "evidence") -Recurse -Force
}

Write-Host "ci-integration.ps1 completed."
