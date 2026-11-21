# windows-2022 installer job driver (B-5).
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root

Write-Host "ci-installer: compile gate (publish server win-x64)"
dotnet publish src/GameClub.Server/GameClub.Server.csproj -c Release -r win-x64 --self-contained true -o artifacts/installer/server-publish --nologo
if ($LASTEXITCODE -ne 0) {
  Write-Error "ci-installer: server publish failed (exit $LASTEXITCODE)"
  exit 1
}
dotnet publish src/GameClub.Kiosk/GameClub.Kiosk.csproj -c Release -f net48 -o artifacts/installer/kiosk-publish --nologo
if ($LASTEXITCODE -ne 0) {
  Write-Error "ci-installer: kiosk publish failed (exit $LASTEXITCODE)"
  exit 1
}

$appVersion = (dotnet msbuild Directory.Build.props -getProperty:Version -nologo | Select-Object -Last 1).Trim()
if ([string]::IsNullOrWhiteSpace($appVersion)) {
  Write-Error "ci-installer: could not read Version from Directory.Build.props"
  exit 1
}
Write-Host "ci-installer: AppVersion=$appVersion"

$serverSize = (Get-ChildItem artifacts/installer/server-publish -Recurse -File | Measure-Object -Property Length -Sum).Sum
$kioskSize = (Get-ChildItem artifacts/installer/kiosk-publish -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host "ci-installer: server publish bytes=$serverSize"
Write-Host "ci-installer: kiosk publish bytes=$kioskSize"

Write-Host "ci-installer: static installer rules"
& (Join-Path $root "installer/tests/NoBackupDelete.Tests.ps1")
& (Join-Path $root "installer/tests/BackupRestoreConfig.Tests.ps1")
& (Join-Path $root "installer/tests/PersianIslKeys.Tests.ps1")
& (Join-Path $root "installer/tests/IsccVersionGate.Tests.ps1")

$expectedInno = [Version]"6.7.1"
. (Join-Path $PSScriptRoot "Resolve-InnoSetupVersion.ps1")

function NormalizeDir([string]$path) {
  if ([string]::IsNullOrWhiteSpace($path)) {
    return $null
  }
  return [System.IO.Path]::GetFullPath($path).TrimEnd('\')
}

function Get-InnoRegistryEntry {
  $registryKeys = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
  )
  foreach ($keyPath in $registryKeys) {
    if (-not (Test-Path -LiteralPath $keyPath)) {
      continue
    }
    $props = Get-ItemProperty -LiteralPath $keyPath -ErrorAction SilentlyContinue
    if ($null -eq $props) {
      continue
    }
    $installLocation = $props.InstallLocation
    if ([string]::IsNullOrWhiteSpace($installLocation)) {
      continue
    }
    return @{
      RegistryKey     = $keyPath
      InstallLocation = NormalizeDir $installLocation
      DisplayVersion  = [string]$props.DisplayVersion
    }
  }
  return $null
}

function Get-IsccPeVersions([string]$isccPath) {
  if (-not (Test-Path -LiteralPath $isccPath)) {
    return $null
  }
  $vi = (Get-Item -LiteralPath $isccPath).VersionInfo
  return @{
    FileVersion    = [string]$vi.FileVersion
    ProductVersion = [string]$vi.ProductVersion
  }
}

function Get-IsccBannerText([string]$isccPath) {
  if (-not (Test-Path -LiteralPath $isccPath)) {
    return $null
  }
  return (& $isccPath 2>&1 | Out-String)
}

$pathIsccShim = Get-Command iscc.exe -ErrorAction SilentlyContinue
$pathIsccResolved = if ($null -ne $pathIsccShim) { $pathIsccShim.Source } else { "(not on PATH)" }

$regEntry = Get-InnoRegistryEntry

if ($null -eq $regEntry) {
  Write-Error @"
ci-installer: Inno Setup $expectedInno required (real ISCC.exe, never Chocolatey PATH shim).
PATH iscc.exe (ignored if Chocolatey shim)=$pathIsccResolved
registry: no Inno Setup 6_is1 uninstall key with InstallLocation found
"@
  exit 1
}

$realInnoDir = $regEntry.InstallLocation
$realIscc = Join-Path $realInnoDir "ISCC.exe"
if (-not (Test-Path -LiteralPath $realIscc)) {
  Write-Error @"
ci-installer: Inno Setup $expectedInno required (real ISCC.exe, never Chocolatey PATH shim).
PATH iscc.exe (ignored if Chocolatey shim)=$pathIsccResolved
resolved ISCC path=$realIscc (missing)
registry key=$($regEntry.RegistryKey)
DisplayVersion=$($regEntry.DisplayVersion)
"@
  exit 1
}
$peVersions = Get-IsccPeVersions $realIscc
$fileVersion = if ($null -ne $peVersions) { $peVersions.FileVersion } else { "(missing ISCC)" }
$productVersion = if ($null -ne $peVersions) { $peVersions.ProductVersion } else { "(missing ISCC)" }

$bannerText = $null
if (Test-Path -LiteralPath $realIscc) {
  $bannerText = Get-IsccBannerText $realIscc
}

$decision = Resolve-InnoVersionDecision `
  -DisplayVersion $regEntry.DisplayVersion `
  -FileVersion $fileVersion `
  -ProductVersion $productVersion `
  -BannerText $bannerText `
  -Expected $expectedInno

Write-Host "ci-installer: resolved ISCC path=$realIscc installDir=$realInnoDir"
Write-Host "ci-installer: registry key=$($regEntry.RegistryKey)"
Write-Host "ci-installer: version source DisplayVersion=$($regEntry.DisplayVersion)"
Write-Host "ci-installer: version source FileVersion=$fileVersion"
Write-Host "ci-installer: version source ProductVersion=$productVersion"
$bannerLine = if ($null -ne $bannerText -and $bannerText -match "(?m)^.*Compiler (engine )?version.*$") { $Matches[0].Trim() } else { "(unparsable or no banner)" }
Write-Host "ci-installer: version source bannerLine=$bannerLine"
Write-Host "ci-installer: version source BannerVersion=$($decision.BannerVersion)"

if (-not $decision.Ok) {
  $contradictionText = if ($decision.Contradictions.Count -gt 0) { $decision.Contradictions -join "; " } else { "(none)" }
  Write-Error @"
ci-installer: Inno Setup $expectedInno required (real ISCC.exe, never Chocolatey PATH shim).
PATH iscc.exe (ignored if Chocolatey shim)=$pathIsccResolved
resolved ISCC path=$realIscc
registry key=$($regEntry.RegistryKey)
DisplayVersion=$($regEntry.DisplayVersion)
FileVersion=$fileVersion
ProductVersion=$productVersion
bannerLine=$bannerLine
BannerVersion=$($decision.BannerVersion)
reason=$($decision.Reason)
contradictions=$contradictionText
"@
  exit 1
}

$issTargets = @(
  (Join-Path $root "installer/server/CheckpointServer.iss"),
  (Join-Path $root "installer/kiosk/CheckpointKiosk.iss")
)
$compiledOutputs = [System.Collections.Generic.List[string]]::new()
foreach ($iss in $issTargets) {
  Write-Host "ci-installer: iscc $iss (via $realIscc) AppVersion=$appVersion"
  $compileLog = & $realIscc "/DAppVersion=$appVersion" $iss 2>&1 | Out-String
  Write-Host $compileLog
  if ($LASTEXITCODE -ne 0) {
    Write-Error "ci-installer: iscc failed for $iss (exit $LASTEXITCODE)"
    exit 1
  }
  if ($compileLog -match 'Resulting Setup program filename is:\s*(.+\.exe)') {
    $compiledOutputs.Add($Matches[1].Trim())
  }
}
if ($compiledOutputs.Count -lt $issTargets.Count) {
  Write-Error "ci-installer: expected $($issTargets.Count) compiled setup exe outputs, got $($compiledOutputs.Count)"
  exit 1
}
foreach ($outPath in ($compiledOutputs | Sort-Object -Unique)) {
  if (-not (Test-Path -LiteralPath $outPath)) {
    Write-Error "ci-installer: expected compiled output missing: $outPath"
    exit 1
  }
  $len = (Get-Item -LiteralPath $outPath).Length
  Write-Host ("ci-installer: compiled output path={0} bytes={1}" -f $outPath, $len)
}
Get-ChildItem installer -Recurse -Filter *.exe | ForEach-Object {
  Write-Host ("ci-installer: artifact {0} bytes={1}" -f $_.Name, $_.Length)
}

Write-Host "ci-installer: done"