# windows-2022 installer job driver — build installers and run B-1..B-13 (B).
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root

Import-Module (Join-Path $root "installer/tools/CheckpointInstall.Common.psm1") -Force

function Invoke-DotnetPublish {
    param([string[]] $DotnetArgs, [string] $Label)
    Write-Host "ci-installer: $Label"
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "ci-installer: $Label failed exit $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}

$appVersion = Get-CheckpointAppVersion -RepoRoot $root
Write-Host "ci-installer: AppVersion=$appVersion"
$lowVersion = "0.0.9"
$high2Version = if ($appVersion -match "^(\d+)\.(\d+)\.(\d+)$") {
    "$($Matches[1]).$($Matches[2]).$([int]$Matches[3] + 1)"
} else { "$appVersion.1" }

$stageServer = Join-Path $root "artifacts/installer/stage-server"
$stageKiosk = Join-Path $root "artifacts/installer/stage-kiosk"
$stageGuard = Join-Path $root "artifacts/installer/stage-guard"
foreach ($d in @($stageServer, $stageKiosk, $stageGuard)) {
    if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $d | Out-Null
}

Invoke-DotnetPublish @(
    "publish", "src/GameClub.Server/GameClub.Server.csproj",
    "-c", "Release", "-r", "win-x64", "--self-contained", "true",
    "-o", $stageServer, "--nologo"
) -Label "publish server win-x64"

$shellProj = Join-Path $root "src/GameClub.Shell/GameClub.Shell.csproj"
if (Test-Path -LiteralPath $shellProj) {
    Invoke-DotnetPublish @(
        "publish", $shellProj,
        "-c", "Release", "-r", "win-x64", "--self-contained", "true",
        "-o", $stageServer, "--nologo"
    ) -Label "publish shell win-x64"
} else {
    Write-Host "ci-installer: GameClub.Shell not present (out of scope); server-only payload"
}

Invoke-DotnetPublish @(
    "publish", "src/GameClub.Kiosk/GameClub.Kiosk.csproj",
    "-c", "Release", "-f", "net48",
    "-o", $stageKiosk, "--nologo"
) -Label "publish kiosk net48"

Invoke-DotnetPublish @(
    "publish", "src/GameClub.Kiosk.Guard/GameClub.Kiosk.Guard.csproj",
    "-c", "Release", "-f", "net48",
    "-o", $stageGuard, "--nologo"
) -Label "publish guard net48"

& (Join-Path $root "installer/tools/Fetch-InstallerThirdParty.ps1") -SkipIfPresent

Write-Host "ci-installer: static installer rules"
& (Join-Path $root "installer/tests/NoBackupDelete.Tests.ps1")
& (Join-Path $root "installer/tests/BackupRestoreConfig.Tests.ps1")
& (Join-Path $root "installer/tests/PersianIslKeys.Tests.ps1")
& (Join-Path $root "installer/tests/IsccVersionGate.Tests.ps1")
& (Join-Path $root "installer/tests/IssAppVersionStatic.Tests.ps1")
& (Join-Path $root "installer/tests/KioskInstallStatic.Tests.ps1")
& (Join-Path $root "installer/tests/BackupDailyTehran.Tests.ps1")
& (Join-Path $root "installer/tests/ServerWizardStatic.Tests.ps1")
& (Join-Path $root "installer/tests/ServerFinishLanStatic.Tests.ps1")
& (Join-Path $root "installer/tests/KioskTrustFileAcl.Tests.ps1")

Write-Host "ci-installer: server finish LAN IPv4 probe (Get-ServerLanIPv4Addresses.ps1)"
$lanProbe = Join-Path $root "installer/tools/Get-ServerLanIPv4Addresses.ps1"
& $lanProbe -Format verbose | ForEach-Object { Write-Host "ci-installer: lan $_" }

$expectedInno = [Version]"6.7.1"
. (Join-Path $PSScriptRoot "Resolve-InnoSetupVersion.ps1")

function NormalizeDir([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    return [System.IO.Path]::GetFullPath($path).TrimEnd('\')
}

function Get-InnoRegistryEntry {
    foreach ($keyPath in @(
            "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
            "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
        )) {
        if (-not (Test-Path -LiteralPath $keyPath)) { continue }
        $props = Get-ItemProperty -LiteralPath $keyPath -ErrorAction SilentlyContinue
        if ($null -eq $props) { continue }
        if ([string]::IsNullOrWhiteSpace($props.InstallLocation)) { continue }
        return @{
            RegistryKey     = $keyPath
            InstallLocation = NormalizeDir $props.InstallLocation
            DisplayVersion  = [string]$props.DisplayVersion
        }
    }
    return $null
}

function Get-IsccPeVersions([string]$isccPath) {
    if (-not (Test-Path -LiteralPath $isccPath)) { return $null }
    $vi = (Get-Item -LiteralPath $isccPath).VersionInfo
    return @{
        FileVersion    = [string]$vi.FileVersion
        ProductVersion = [string]$vi.ProductVersion
    }
}

function Get-IsccBannerText([string]$isccPath) {
    if (-not (Test-Path -LiteralPath $isccPath)) { return $null }
    return (& $isccPath 2>&1 | Out-String)
}

$pathIsccShim = Get-Command iscc.exe -ErrorAction SilentlyContinue
$pathIsccResolved = if ($null -ne $pathIsccShim) { $pathIsccShim.Source } else { "(not on PATH)" }

$regEntry = Get-InnoRegistryEntry
if ($null -eq $regEntry) {
    Write-Error "ci-installer: Inno Setup registry entry missing (PATH iscc=$pathIsccResolved)"
    exit 1
}

$realIscc = Join-Path $regEntry.InstallLocation "ISCC.exe"
if (-not (Test-Path -LiteralPath $realIscc)) {
    Write-Error "ci-installer: ISCC missing at $realIscc"
    exit 1
}

$peVersions = Get-IsccPeVersions $realIscc
$fileVersion = if ($null -ne $peVersions) { $peVersions.FileVersion } else { "(missing ISCC)" }
$productVersion = if ($null -ne $peVersions) { $peVersions.ProductVersion } else { "(missing ISCC)" }
$bannerText = Get-IsccBannerText $realIscc

$decision = Resolve-InnoVersionDecision `
    -DisplayVersion $regEntry.DisplayVersion `
    -FileVersion $fileVersion `
    -ProductVersion $productVersion `
    -BannerText $bannerText `
    -Expected $expectedInno

Write-Host "ci-installer: resolved ISCC path=$realIscc"
Write-Host "ci-installer: DisplayVersion=$($regEntry.DisplayVersion) BannerVersion=$($decision.BannerVersion)"

if (-not $decision.Ok) {
    Write-Error "ci-installer: Inno Setup $expectedInno required; reason=$($decision.Reason)"
    exit 1
}

function Invoke-IsccCompile {
    param([string] $IssPath, [string] $Version)
    $defines = @(
        "/DAppVersion=$Version",
        "/DStagingServerDir=$stageServer",
        "/DStagingPgDir=$(Join-Path $root 'artifacts/installer/stage-pg-trimmed')",
        "/DStagingWebView2=$(Join-Path $root 'artifacts/installer/stage-webview2/MicrosoftEdgeWebView2RuntimeInstallerX64.exe')",
        "/DStagingKioskDir=$stageKiosk",
        "/DStagingGuardDir=$stageGuard"
    )
    Write-Host "ci-installer: iscc $IssPath AppVersion=$Version"
    $compileLog = & $realIscc $defines $IssPath 2>&1 | Out-String
    Write-Host $compileLog
    if ($LASTEXITCODE -ne 0) { throw "iscc failed $IssPath" }
    if ($compileLog -match 'Resulting Setup program filename is:\s*(.+\.exe)') { return $Matches[1].Trim() }
    throw "iscc output parse failed"
}

$serverIss = Join-Path $root "installer/server/CheckpointServer.iss"
$kioskIss = Join-Path $root "installer/kiosk/CheckpointKiosk.iss"

$serverLow = Invoke-IsccCompile -IssPath $serverIss -Version $lowVersion
$serverHigh = Invoke-IsccCompile -IssPath $serverIss -Version $appVersion
$serverHigh2 = Invoke-IsccCompile -IssPath $serverIss -Version $high2Version
$kioskLow = Invoke-IsccCompile -IssPath $kioskIss -Version $lowVersion
$kioskHigh = Invoke-IsccCompile -IssPath $kioskIss -Version $appVersion

foreach ($outPath in @($serverLow, $serverHigh, $serverHigh2, $kioskLow, $kioskHigh)) {
    $len = (Get-Item -LiteralPath $outPath).Length
    Write-Host ("ci-installer: installer path={0} bytes={1}" -f $outPath, $len)
}

$logRoot = Join-Path $root "artifacts/installer/acceptance-logs"
$accounts = Join-Path $root "installer/ci/accounts-silent.json"
$acceptance = Join-Path $root "installer/tools/acceptance"

& (Join-Path $acceptance "Invoke-B2-Upgrade.ps1") -LowSetup $serverLow -HighSetup $serverHigh -AccountsFile $accounts -LowVersion $lowVersion -HighVersion $appVersion -Mode WithoutOpenSessions
& (Join-Path $acceptance "Invoke-B2-Upgrade.ps1") -LowSetup $serverHigh -HighSetup $serverHigh2 -AccountsFile $accounts -LowVersion $appVersion -HighVersion $high2Version -Mode WithOpenSessions -SkipDowngradeCheck
& (Join-Path $acceptance "Invoke-B1-CleanServerInstall.ps1") -ServerSetup $serverHigh -AccountsFile $accounts -LogsDir $logRoot
& (Join-Path $acceptance "Invoke-B3-BackupNeverDeletes.ps1") -LogsDir $logRoot
& (Join-Path $acceptance "Invoke-B4-UninstallReinstall.ps1") -ServerSetup $serverHigh -AccountsFile $accounts
& (Join-Path $acceptance "Invoke-B5-KioskInstall.ps1") -KioskSetup $kioskHigh -LogsDir $logRoot
& (Join-Path $acceptance "Invoke-B6-B12-KioskFirstRun.ps1") -RepoRoot $root
& (Join-Path $acceptance "Invoke-B7-KioskUpgrade.ps1") -LowKioskSetup $kioskLow -HighKioskSetup $kioskHigh
& (Join-Path $acceptance "Invoke-B8-WindowsBillingRestart.ps1") -AccountsFile $accounts
& (Join-Path $acceptance "Invoke-B13-DefaultPinUpgrade.ps1") -ServerSetup $serverHigh -AccountsFile $accounts

Copy-Item -Force "C:\ProgramData\Checkpoint\logs\upgrade.log" (Join-Path $logRoot "upgrade.log") -ErrorAction SilentlyContinue
Write-Host "ci-installer: done"