$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

$artifactDir = Join-Path $env:GITHUB_WORKSPACE "artifacts"
if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) {
    $artifactDir = Join-Path $repoRoot "artifacts"
}
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null
$logDir = Join-Path $artifactDir "shell-windows-logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$env:GAMECLUB_DATABASE = $env:GAMECLUB_DATABASE ?? "Host=127.0.0.1;Port=5432;Database=gameclub_test;Username=postgres;Password=postgres;SSL Mode=Disable"
$env:GAMECLUB_API_KEY = $env:GAMECLUB_API_KEY ?? "ci-test-key"
$env:OperatorSessionSecret = $env:OperatorSessionSecret ?? "ci-operator-secret-r11"

Write-Host "Publishing GameClub.Server win-x64"
dotnet publish src/GameClub.Server/GameClub.Server.csproj -c Release -f net10.0 -r win-x64 `
  /p:PublishProfile=win-x64 /p:SelfContained=true /p:PublishTrimmed=false --nologo

$publishDir = Resolve-Path "src/GameClub.Server/bin/Release/net10.0/win-x64/publish"
$serverExe = Join-Path $publishDir "GameClub.Server.exe"

if (-not (Get-Service -Name CheckpointServer -ErrorAction SilentlyContinue)) {
    sc.exe create CheckpointServer binPath= "`"$serverExe`"" start= delayed-auto | Out-Null
}

dotnet build src/GameClub.Shell/GameClub.Shell.csproj -c Release -f net48 --nologo

$wv2 = (Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" -ErrorAction SilentlyContinue).pv
if (-not $wv2) {
    choco install microsoft-edge-webview2-runtime -y --no-progress
}

dotnet build tests/GameClub.Shell.Windows.IntegrationTests/GameClub.Shell.Windows.IntegrationTests.csproj -c Release -f net48 --nologo
dotnet test tests/GameClub.Shell.Windows.IntegrationTests/GameClub.Shell.Windows.IntegrationTests.csproj `
  -c Release -f net48 --no-build --nologo `
  --filter "Category=WindowsShell" `
  --logger "console;verbosity=normal" `
  --results-directory $artifactDir `
  --logger "trx;LogFileName=shell-windows.trx"

if (Test-Path (Join-Path $env:LOCALAPPDATA "Checkpoint\Shell\window.json")) {
    Copy-Item (Join-Path $env:LOCALAPPDATA "Checkpoint\Shell\window.json") (Join-Path $artifactDir "window.json") -Force
}