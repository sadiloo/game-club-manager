$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

$artifactDir = Join-Path $env:GITHUB_WORKSPACE "artifacts"
if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE)) {
    $artifactDir = Join-Path $repoRoot "artifacts"
}
New-Item -ItemType Directory -Force -Path $artifactDir | Out-Null

if (-not $env:GAMECLUB_TEST_DATABASE) {
    $env:GAMECLUB_TEST_DATABASE = "Host=localhost;Port=5432;Database=gameclub_test;Username=postgres;Password=postgres;SSL Mode=Disable"
    $env:GAMECLUB_DATABASE = $env:GAMECLUB_TEST_DATABASE
    $env:GAMECLUB_API_KEY = "ci-test-key"
}

if (-not $env:CHECKPOINT_SERVER_URL) {
    $env:CHECKPOINT_SERVER_URL = "http://127.0.0.1:5225"
}
if (-not $env:OperatorSessionSecret) {
    $env:OperatorSessionSecret = "ci-operator-secret-r11"
}
if (-not $env:ASPNETCORE_URLS) {
    $env:ASPNETCORE_URLS = $env:CHECKPOINT_SERVER_URL
}
$env:ASPNETCORE_ENVIRONMENT = "Production"

$kioskAccountName = "CheckpointKiosk"
$guestUser = "CheckpointGuest"
$guestPassword = "CkPt!" + ([Guid]::NewGuid().ToString("N").Substring(0, 16))
$kioskPassword = "CkPt!" + ([Guid]::NewGuid().ToString("N").Substring(0, 16))

function Ensure-LocalUser([string]$name, [string]$password) {
    $existing = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
    if ($existing) {
        Set-LocalUser -Name $name -Password (ConvertTo-SecureString $password -AsPlainText -Force)
        return
    }
    New-LocalUser -Name $name -Password (ConvertTo-SecureString $password -AsPlainText -Force) -UserMayNotChangePassword -PasswordNeverExpires | Out-Null
    Add-LocalGroupMember -Group "Users" -Member $name -ErrorAction SilentlyContinue
}

function Ensure-KioskRegistryHiveLoaded([string]$accountName, [string]$sid) {
    $profileDir = Join-Path $env:SystemDrive "Users\$accountName"
    $ntuser = Join-Path $profileDir "NTUSER.DAT"
    if (-not (Test-Path $profileDir)) {
        New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
    }
    if (-not (Test-Path $ntuser)) {
        $defaultHive = Join-Path $env:SystemDrive "Users\Default\NTUSER.DAT"
        if (-not (Test-Path $defaultHive)) {
            throw "Default user hive missing; cannot load $accountName policy hive."
        }
        Copy-Item -Path $defaultHive -Destination $ntuser -Force
    }
    $hiveRoot = "HKU\$sid"
    reg query $hiveRoot 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        reg load $hiveRoot $ntuser | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "reg load failed for $accountName ($sid)" }
    }
}

try {
    Ensure-LocalUser -name $kioskAccountName -password $kioskPassword
    Ensure-LocalUser -name $guestUser -password $guestPassword
    $guestProbe = Get-LocalUser -Name $guestUser -ErrorAction Stop
    if ($guestProbe.Enabled -eq $false) { Enable-LocalUser -Name $guestUser }
} catch {
    Write-Error "Local user provisioning failed: $_"
    exit 1
}

$kioskSid = (New-Object System.Security.Principal.NTAccount($kioskAccountName)).Translate([System.Security.Principal.SecurityIdentifier]).Value
Ensure-KioskRegistryHiveLoaded -accountName $kioskAccountName -sid $kioskSid
$env:CHECKPOINT_KIOSK_SID = $kioskSid
# Runner session launches Guard/shell; DACL deny + non-admin terminate proofs use CheckpointKiosk SID above.
$env:CHECKPOINT_KIOSK_USER = $env:USERNAME
$env:CHECKPOINT_SECOND_USER = $guestUser
$env:CHECKPOINT_SECOND_PASSWORD = $guestPassword

$env:PGPASSWORD = "postgres"

Write-Host "Building Release (-warnaserror)..."
dotnet build GameClub.slnx -c Release -warnaserror --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build src/GameClub.Kiosk/GameClub.Kiosk.csproj -c Release -f net48 --nologo
dotnet build src/GameClub.Kiosk.Guard/GameClub.Kiosk.Guard.csproj -c Release -f net48 --nologo
dotnet build src/GameClub.Server/GameClub.Server.csproj -c Release --nologo
dotnet build tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj -c Release -f net48 --nologo
dotnet build tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj -c Release -f net10.0 --nologo

$serverDll = Join-Path $repoRoot "src/GameClub.Server/bin/Release/net10.0/GameClub.Server.dll"
if (-not (Test-Path $serverDll)) { throw "GameClub.Server.dll not found at $serverDll" }
$serverLog = Join-Path $artifactDir "kiosk-windows-logs/server-out.log"
New-Item -ItemType Directory -Force -Path (Split-Path $serverLog) | Out-Null
$serverErr = Join-Path (Split-Path $serverLog) "server-err.log"
$serverProc = Start-Process -FilePath "dotnet" -ArgumentList @("exec", $serverDll) -PassThru -NoNewWindow -RedirectStandardOutput $serverLog -RedirectStandardError $serverErr -WorkingDirectory $repoRoot
$env:GAMECLUB_SERVER_PID = $serverProc.Id

$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        $r = Invoke-WebRequest -Uri "$($env:CHECKPOINT_SERVER_URL)/api/stations" -Headers @{ "X-Api-Key" = $env:GAMECLUB_API_KEY } -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { $ready = $true; break }
    } catch {
        Start-Sleep -Seconds 2
    }
}
if (-not $ready) {
    Stop-Process -Id $serverProc.Id -Force -ErrorAction SilentlyContinue
    throw "GameClub.Server did not become ready on $($env:CHECKPOINT_SERVER_URL)"
}

Write-Host "Running windows server integration (net10.0, live server)..."
dotnet test tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj `
    -c Release -f net10.0 --no-build --nologo `
    --logger "trx;LogFileName=kiosk-windows-server.trx" `
    --results-directory $artifactDir
$serverTestExit = $LASTEXITCODE

Write-Host "Running windows guard/wpf integration (net48, live server env)..."
dotnet test tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj `
    -c Release -f net48 --no-build --nologo `
    --logger "trx;LogFileName=kiosk-windows-guard.trx" `
    --results-directory $artifactDir
$guardTestExit = $LASTEXITCODE

try { Stop-Process -Id $serverProc.Id -Force -ErrorAction SilentlyContinue } catch { }

$watchdog = Join-Path $env:ProgramData "Checkpoint\guard\watchdog.log"
if (Test-Path $watchdog) {
    Copy-Item $watchdog (Join-Path $artifactDir "watchdog.log") -Force
}

$evidence = Join-Path $env:ProgramData "Checkpoint\evidence"
if (Test-Path $evidence) {
    Copy-Item $evidence (Join-Path $artifactDir "evidence") -Recurse -Force
}

if ($serverTestExit -ne 0) { exit $serverTestExit }
if ($guardTestExit -ne 0) { exit $guardTestExit }

Write-Host "ci-integration.ps1 completed."
