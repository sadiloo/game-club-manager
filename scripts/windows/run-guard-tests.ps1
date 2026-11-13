#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Runs Windows Guard system integration tests on a disposable QA Windows VM.

.DESCRIPTION
    Builds kiosk, Guard, and the Windows integration test project (net48), provisions
    local users and kiosk registry hive prerequisites, then runs only tests tagged
    Category=WindowsGuard.

    WARNING: These tests change registry DisallowRun and Explorer policies, modify
    process DACLs, and kill processes. Run only on a disposable QA Windows VM — never
    on a cafe PC or production kiosk.

.PARAMETER ResultsDir
    Directory for TRX and test output. Default: <repo>/artifacts/guard-qa-results

.EXAMPLE
    pwsh -ExecutionPolicy Bypass -File scripts/windows/run-guard-tests.ps1
#>
param(
    [string]$ResultsDir = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($ResultsDir)) {
    $ResultsDir = Join-Path $repoRoot "artifacts/guard-qa-results"
}
New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null

$kioskAccountName = "CheckpointKiosk"
$guestUser = "CheckpointGuest"
$script:HiveLoadedByScript = $false
$script:LoadedHiveSid = $null
$testExitCode = 0

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
            $defaultHive = Join-Path $env:windir "System32\config\Default"
        }
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
        $script:HiveLoadedByScript = $true
        $script:LoadedHiveSid = $sid
    }
}

function Unload-KioskRegistryHiveIfLoaded {
    if (-not $script:HiveLoadedByScript -or [string]::IsNullOrWhiteSpace($script:LoadedHiveSid)) {
        return
    }
    $hiveRoot = "HKU\$($script:LoadedHiveSid)"
    reg unload $hiveRoot 2>$null | Out-Null
    $script:HiveLoadedByScript = $false
    $script:LoadedHiveSid = $null
}

try {
    $kioskPassword = "CkPt!" + ([Guid]::NewGuid().ToString("N").Substring(0, 16))
    $guestPassword = if (-not [string]::IsNullOrWhiteSpace($env:CHECKPOINT_SECOND_PASSWORD)) {
        $env:CHECKPOINT_SECOND_PASSWORD
    } else {
        "CkPt!" + ([Guid]::NewGuid().ToString("N").Substring(0, 16))
    }

    if ([string]::IsNullOrWhiteSpace($env:CHECKPOINT_SECOND_USER)) {
        $env:CHECKPOINT_SECOND_USER = $guestUser
    } else {
        $guestUser = $env:CHECKPOINT_SECOND_USER
    }
    if ([string]::IsNullOrWhiteSpace($env:CHECKPOINT_SECOND_PASSWORD)) {
        $env:CHECKPOINT_SECOND_PASSWORD = $guestPassword
    }

    Ensure-LocalUser -name $kioskAccountName -password $kioskPassword
    Ensure-LocalUser -name $guestUser -password $guestPassword
    $guestProbe = Get-LocalUser -Name $guestUser -ErrorAction Stop
    if ($guestProbe.Enabled -eq $false) { Enable-LocalUser -Name $guestUser }

    if ([string]::IsNullOrWhiteSpace($env:CHECKPOINT_KIOSK_SID)) {
        $kioskSid = (New-Object System.Security.Principal.NTAccount($kioskAccountName)).Translate([System.Security.Principal.SecurityIdentifier]).Value
        Ensure-KioskRegistryHiveLoaded -accountName $kioskAccountName -sid $kioskSid
        $env:CHECKPOINT_KIOSK_SID = $kioskSid
    } else {
        $kioskSid = $env:CHECKPOINT_KIOSK_SID
        $loadAccount = $kioskAccountName
        if (-not [string]::IsNullOrWhiteSpace($env:CHECKPOINT_KIOSK_USER)) {
            $loadAccount = $env:CHECKPOINT_KIOSK_USER
        }
        Ensure-KioskRegistryHiveLoaded -accountName $loadAccount -sid $kioskSid
    }

    if ([string]::IsNullOrWhiteSpace($env:CHECKPOINT_KIOSK_USER)) {
        $loggedOnUser = (Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction SilentlyContinue).UserName
        if (-not [string]::IsNullOrWhiteSpace($loggedOnUser)) {
            $env:CHECKPOINT_KIOSK_USER = $loggedOnUser.Split('\')[-1]
        } else {
            $env:CHECKPOINT_KIOSK_USER = $env:USERNAME
        }
    }

    if ([string]::IsNullOrWhiteSpace($env:CHECKPOINT_GUARD_CI_RUNNER_LAUNCH)) {
        $env:CHECKPOINT_GUARD_CI_RUNNER_LAUNCH = "1"
    }

    Write-Host "Building Release (Kiosk, Guard, integration tests net48)..."
    $builds = @(
        @("build", "src/GameClub.Kiosk/GameClub.Kiosk.csproj", "-c", "Release", "-f", "net48", "--nologo"),
        @("build", "src/GameClub.Kiosk.Guard/GameClub.Kiosk.Guard.csproj", "-c", "Release", "-f", "net48", "--nologo"),
        @("build", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj", "-c", "Release", "-f", "net48", "--nologo")
    )
    foreach ($buildArgs in $builds) {
        & dotnet @buildArgs
        if ($LASTEXITCODE -ne 0) {
            $testExitCode = $LASTEXITCODE
            throw "build failed"
        }
    }

    Write-Host "Running Windows Guard system tests (Category=WindowsGuard)..."
    $testArgs = @(
        "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj",
        "-c", "Release", "-f", "net48", "--no-build", "--nologo",
        "--filter", "Category=WindowsGuard",
        "--logger", "trx;LogFileName=kiosk-windows-guard-qa.trx",
        "--results-directory", $ResultsDir,
        "--blame-hang", "--blame-hang-timeout", "5m", "--blame-hang-dump-type", "none",
        "--logger", "console;verbosity=detailed"
    )
    & dotnet @testArgs
    $testExitCode = $LASTEXITCODE
} catch {
    Write-Host "run-guard-tests.ps1 error: $($_.Exception.Message)"
    if ($testExitCode -eq 0) { $testExitCode = 1 }
} finally {
    Unload-KioskRegistryHiveIfLoaded
}

Write-Host ""
Write-Host "After a failed run, verify Explorer DisallowRun policy state (User Configuration / Administrative Templates / System / Don't run specified Windows applications)."
Write-Host "If firewall or policy cleanup is needed, run: pwsh -File scripts/windows/ci-firewall-restore.ps1 -Restore"
Write-Host "Optional evidence snapshot: pwsh -File scripts/windows/ci-firewall-restore.ps1 -Evidence"

exit $testExitCode
