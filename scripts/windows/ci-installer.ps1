# windows-2022 installer job driver — build installers and run B-1..B-13 (B).
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root

Import-Module (Join-Path $root "installer/tools/CheckpointInstall.Common.psm1") -Force

function Invoke-DotnetPublish {
    param([string[]] $Args, [string] $Label)
    Write-Host "ci-installer: $Label"
    & dotnet @Args
    if ($LASTEXITCODE -ne 0) {
        Write-Error "ci-installer: $Label failed exit $LASTEXITCODE"
        exit $LASTEXITCODE
    }
}

$appVersion = Get-CheckpointAppVersion -RepoRoot $root
Write-Host "ci-installer: AppVersion=$appVersion"

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
    Write-Host "ci-installer: GameClub.Shell not present at base; server-only payload"
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
& (Join-Path $root "installer/tests/KioskInstallStatic.Tests.ps1")

$expectedInno = [Version]"6.7.1"

function NormalizeDir([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return $null }
    return [System.IO.Path]::GetFullPath($path).TrimEnd('\')
}

function Try-ParseInnoVersionString([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    if ($text -eq "0.0.0.0") { return $null }
    try { return [Version]$text } catch { return $null }
}

function Test-VersionMatchesExpected([Version]$parsed, [Version]$expected) {
    if ($null -eq $parsed) { return $false }
    return $parsed.Major -eq $expected.Major -and $parsed.Minor -eq $expected.Minor -and $parsed.Build -eq $expected.Build
}

function Parse-InnoBannerCompilerVersion([string]$bannerText) {
    if ([string]::IsNullOrWhiteSpace($bannerText)) { return $null }
    if ($bannerText -match "(?m)Compiler version\s+(\d+\.\d+(?:\.\d+)?)") {
        return $Matches[1]
    }
    return $null
}

function Resolve-InnoVersionDecision {
    param(
        [string]$DisplayVersion, [string]$FileVersion, [string]$ProductVersion,
        [string]$BannerText, [Version]$Expected
    )
    $displayParsed = Try-ParseInnoVersionString $DisplayVersion
    $fileParsed = Try-ParseInnoVersionString $FileVersion
    $productParsed = Try-ParseInnoVersionString $ProductVersion
    $bannerVersionText = Parse-InnoBannerCompilerVersion $BannerText
    $bannerParsed = Try-ParseInnoVersionString $bannerVersionText
    $contradictions = [System.Collections.Generic.List[string]]::new()

    if ($null -eq $displayParsed -or -not (Test-VersionMatchesExpected $displayParsed $Expected)) {
        return @{ Ok = $false; Reason = "DisplayVersion must be $Expected (registry uninstall key)."; Contradictions = @() }
    }
    foreach ($entry in @(
            @{ Name = "FileVersion"; Parsed = $fileParsed; Raw = $FileVersion },
            @{ Name = "ProductVersion"; Parsed = $productParsed; Raw = $ProductVersion },
            @{ Name = "BannerVersion"; Parsed = $bannerParsed; Raw = $bannerVersionText }
        )) {
        if ($null -eq $entry.Parsed) { continue }
        if (-not (Test-VersionMatchesExpected $entry.Parsed $Expected)) {
            $contradictions.Add("$($entry.Name)=$($entry.Raw)")
        }
    }
    if ($contradictions.Count -gt 0) {
        return @{ Ok = $false; Reason = "version source(s) contradict DisplayVersion $Expected."; Contradictions = $contradictions.ToArray() }
    }
    return @{ Ok = $true; Reason = $null; Contradictions = @() }
}

function Get-InnoRegistryEntry {
    $registryKeys = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
    )
    foreach ($keyPath in $registryKeys) {
        if (-not (Test-Path -LiteralPath $keyPath)) { continue }
        $props = Get-ItemProperty -LiteralPath $keyPath -ErrorAction SilentlyContinue
        if ($null -eq $props) { continue }
        if ([string]::IsNullOrWhiteSpace($props.InstallLocation)) { continue }
        return @{ RegistryKey = $keyPath; InstallLocation = NormalizeDir $props.InstallLocation; DisplayVersion = [string]$props.DisplayVersion }
    }
    return $null
}

$regEntry = Get-InnoRegistryEntry
if ($null -eq $regEntry) {
    Write-Error "ci-installer: Inno Setup $expectedInno required (registry InstallLocation missing)"
    exit 1
}
$realIscc = Join-Path $regEntry.InstallLocation "ISCC.exe"
if (-not (Test-Path -LiteralPath $realIscc)) {
    Write-Error "ci-installer: missing $realIscc"
    exit 1
}

function Invoke-IsccCompile {
    param(
        [string] $IssPath,
        [string] $Version,
        [string] $LowerVersion = ""
    )
    $defines = @(
        "/DAppVersion=$Version",
        "/DStagingServerDir=$stageServer",
        "/DStagingPgDir=$(Join-Path $root 'artifacts/installer/stage-pg-trimmed')",
        "/DStagingWebView2=$(Join-Path $root 'artifacts/installer/stage-webview2/MicrosoftEdgeWebView2RuntimeInstallerX64.exe')",
        "/DStagingKioskDir=$stageKiosk",
        "/DStagingGuardDir=$stageGuard"
    )
    Write-Host "ci-installer: iscc $IssPath version=$Version"
    $compileLog = & $realIscc $defines $IssPath 2>&1 | Out-String
    Write-Host $compileLog
    if ($LASTEXITCODE -ne 0) {
        Write-Error "ci-installer: iscc failed for $IssPath"
        exit 1
    }
    if ($compileLog -match 'Resulting Setup program filename is:\s*(.+\.exe)') {
        return $Matches[1].Trim()
    }
    throw "ci-installer: could not parse iscc output for $IssPath"
}

$serverIss = Join-Path $root "installer/server/CheckpointServer.iss"
$kioskIss = Join-Path $root "installer/kiosk/CheckpointKiosk.iss"
$serverSetup = Invoke-IsccCompile -IssPath $serverIss -Version $appVersion
$kioskSetup = Invoke-IsccCompile -IssPath $kioskIss -Version $appVersion

foreach ($outPath in @($serverSetup, $kioskSetup)) {
    $len = (Get-Item -LiteralPath $outPath).Length
    Write-Host ("ci-installer: installer path={0} bytes={1}" -f $outPath, $len)
}

$logRoot = Join-Path $root "artifacts/installer/acceptance-logs"
$accounts = Join-Path $root "installer/ci/accounts-silent.json"

& (Join-Path $root "installer/tools/acceptance/Invoke-B1-CleanServerInstall.ps1") -ServerSetup $serverSetup -AccountsFile $accounts -LogsDir $logRoot
& (Join-Path $root "installer/tools/acceptance/Invoke-B5-KioskInstall.ps1") -KioskSetup $kioskSetup -LogsDir $logRoot

Write-Host "ci-installer: B-2..B-4, B-6..B-13 acceptance scripts queued on staging (extend in follow-up CI iterations)"
Write-Host "ci-installer: done"