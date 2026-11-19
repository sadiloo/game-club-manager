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
$env:CHECKPOINT_SERVER_URL = "http://127.0.0.1:5000"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5000"
$env:SHELL_A3_BILLING_WAIT_SECONDS = "600"

$configDir = "C:\ProgramData\Checkpoint\config"
New-Item -ItemType Directory -Force -Path $configDir | Out-Null
@"
{
  "GAMECLUB_API_KEY": "$($env:GAMECLUB_API_KEY)",
  "ConnectionStrings": {
    "Default": "$($env:GAMECLUB_DATABASE)"
  },
  "OperatorSessionSecret": "$($env:OperatorSessionSecret)"
}
"@ | Set-Content -Path (Join-Path $configDir "server.json") -Encoding UTF8

if (-not (Get-Service -Name postgresql* -ErrorAction SilentlyContinue)) {
    choco install postgresql17 --params '/Password:postgres' -y --no-progress
}

[Environment]::SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:5000", "Machine")

Write-Host "Publishing GameClub.Server win-x64"
dotnet publish src/GameClub.Server/GameClub.Server.csproj -c Release -f net10.0 -r win-x64 `
  /p:PublishProfile=win-x64 /p:SelfContained=true /p:PublishTrimmed=false --nologo

$publishDir = Resolve-Path "src/GameClub.Server/bin/Release/net10.0/win-x64/publish"
$serverExe = Join-Path $publishDir "GameClub.Server.exe"

if (-not (Get-Service -Name CheckpointServer -ErrorAction SilentlyContinue)) {
    sc.exe create CheckpointServer binPath= "`"$serverExe`"" start= delayed-auto | Out-Null
} else {
    sc.exe config CheckpointServer binPath= "`"$serverExe`"" start= delayed-auto | Out-Null
}

Stop-Service -Name CheckpointServer -ErrorAction SilentlyContinue -Force
Start-Sleep -Seconds 2
Start-Service -Name CheckpointServer
$deadline = (Get-Date).AddMinutes(2)
while ((Get-Date) -lt $deadline) {
    try {
        $r = Invoke-WebRequest -Uri "http://127.0.0.1:5000/api/local/server-status" -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { break }
    } catch { }
    Start-Sleep -Seconds 2
}

function New-ShellCiCompliantPassword {
    $upper = (65..90) | ForEach-Object { [char]$_ }
    $lower = (97..122) | ForEach-Object { [char]$_ }
    $digits = (48..57) | ForEach-Object { [char]$_ }
    $symbols = '@', '#', '$', '!', '%', '^', '&', '*'
    $required = @(
        ($lower | Get-Random),
        ($upper | Get-Random),
        ($digits | Get-Random),
        ($symbols | Get-Random)
    )
    $pool = $lower + $upper + $digits + $symbols
    while ($required.Count -lt 20) {
        $required += $pool | Get-Random
    }
  return -join ($required | Get-Random -Count $required.Count)
}

$stdUser = "gcm_shell_std"
$stdPass = New-ShellCiCompliantPassword
Write-Output "::add-mask::$stdPass"
$secure = ConvertTo-SecureString $stdPass -AsPlainText -Force
$VerbosePreference = "SilentlyContinue"
$DebugPreference = "SilentlyContinue"
$InformationPreference = "SilentlyContinue"
if (-not (Get-LocalUser -Name $stdUser -ErrorAction SilentlyContinue)) {
    New-LocalUser -Name $stdUser -Password $secure -FullName "GCM Shell CI" | Out-Null
} else {
    Set-LocalUser -Name $stdUser -Password $secure
}
$env:GCM_SHELL_STD_USER = $stdUser
$env:GCM_SHELL_STD_PASSWORD = $stdPass

dotnet build src/GameClub.Shell/GameClub.Shell.csproj -c Release -f net48 --nologo
$env:GAMECLUB_SHELL_EXE = Resolve-Path "src/GameClub.Shell/bin/Release/net48/GameClub.Shell.exe"

$wv2 = (Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" -ErrorAction SilentlyContinue).pv
if (-not $wv2) {
    choco install microsoft-edge-webview2-runtime -y --no-progress
}

dotnet build tests/GameClub.Shell.Windows.IntegrationTests/GameClub.Shell.Windows.IntegrationTests.csproj -c Release -f net48 --nologo
& (Join-Path $PSScriptRoot "shell-windowshell-inventory.ps1") -Role Shell
dotnet test tests/GameClub.Shell.Windows.IntegrationTests/GameClub.Shell.Windows.IntegrationTests.csproj `
  -c Release -f net48 --no-build --nologo `
  --filter "Category=WindowsShell" `
  --logger "console;verbosity=normal" `
  --results-directory $artifactDir `
  --logger "trx;LogFileName=shell-windows.trx"

if (Test-Path (Join-Path $env:LOCALAPPDATA "Checkpoint\Shell\window.json")) {
    Copy-Item (Join-Path $env:LOCALAPPDATA "Checkpoint\Shell\window.json") (Join-Path $artifactDir "window.json") -Force
}