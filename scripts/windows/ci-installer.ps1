# windows-2022 installer job driver (B-5).
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root

Write-Host "ci-installer: compile gate (publish server win-x64)"
dotnet publish src/GameClub.Server/GameClub.Server.csproj -c Release -r win-x64 --self-contained true -o artifacts/installer/server-publish --nologo
dotnet publish src/GameClub.Kiosk/GameClub.Kiosk.csproj -c Release -f net48 -o artifacts/installer/kiosk-publish --nologo

$serverSize = (Get-ChildItem artifacts/installer/server-publish -Recurse -File | Measure-Object -Property Length -Sum).Sum
$kioskSize = (Get-ChildItem artifacts/installer/kiosk-publish -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host "ci-installer: server publish bytes=$serverSize"
Write-Host "ci-installer: kiosk publish bytes=$kioskSize"

Write-Host "ci-installer: static installer rules"
& (Join-Path $root "installer/tests/NoBackupDelete.Tests.ps1")

$expectedInno = [Version]"6.7.1"

function NormalizeDir([string]$path) {
  if ([string]::IsNullOrWhiteSpace($path)) {
    return $null
  }
  return [System.IO.Path]::GetFullPath($path).TrimEnd('\')
}

function Try-ParseInnoVersionString([string]$text) {
  if ([string]::IsNullOrWhiteSpace($text)) {
    return $null
  }
  if ($text -eq "0.0.0.0") {
    return $null
  }
  try {
    return [Version]$text
  }
  catch {
    return $null
  }
}

function Test-VersionMatchesExpected([Version]$parsed, [Version]$expected) {
  if ($null -eq $parsed) {
    return $false
  }
  return $parsed.Major -eq $expected.Major `
    -and $parsed.Minor -eq $expected.Minor `
    -and $parsed.Build -eq $expected.Build
}

function Parse-InnoBannerCompilerVersion([string]$bannerText) {
  if ([string]::IsNullOrWhiteSpace($bannerText)) {
    return $null
  }
  if ($bannerText -match "(?m)Compiler version\s+(\d+\.\d+(?:\.\d+)?)") {
    return $Matches[1]
  }
  return $null
}

function Resolve-InnoVersionDecision {
  param(
    [string]$DisplayVersion,
    [string]$FileVersion,
    [string]$ProductVersion,
    [string]$BannerText,
    [Version]$Expected
  )

  $displayParsed = Try-ParseInnoVersionString $DisplayVersion
  $fileParsed = Try-ParseInnoVersionString $FileVersion
  $productParsed = Try-ParseInnoVersionString $ProductVersion
  $bannerVersionText = Parse-InnoBannerCompilerVersion $BannerText
  $bannerParsed = Try-ParseInnoVersionString $bannerVersionText

  $contradictions = [System.Collections.Generic.List[string]]::new()

  if ($null -eq $displayParsed -or -not (Test-VersionMatchesExpected $displayParsed $Expected)) {
    return @{
      Ok               = $false
      Reason           = "DisplayVersion must be $Expected (registry uninstall key)."
      DisplayVersion   = $DisplayVersion
      FileVersion      = $FileVersion
      ProductVersion   = $ProductVersion
      BannerVersion    = $bannerVersionText
      Contradictions   = @()
    }
  }

  foreach ($entry in @(
      @{ Name = "FileVersion"; Parsed = $fileParsed; Raw = $FileVersion }
      @{ Name = "ProductVersion"; Parsed = $productParsed; Raw = $ProductVersion }
      @{ Name = "BannerVersion"; Parsed = $bannerParsed; Raw = $bannerVersionText }
    )) {
    if ($null -eq $entry.Parsed) {
      continue
    }
    if (-not (Test-VersionMatchesExpected $entry.Parsed $Expected)) {
      $contradictions.Add("$($entry.Name)=$($entry.Raw)")
    }
  }

  if ($contradictions.Count -gt 0) {
    return @{
      Ok               = $false
      Reason           = "version source(s) contradict DisplayVersion $Expected."
      DisplayVersion   = $DisplayVersion
      FileVersion      = $FileVersion
      ProductVersion   = $ProductVersion
      BannerVersion    = $bannerVersionText
      Contradictions   = $contradictions.ToArray()
    }
  }

  return @{
    Ok               = $true
    Reason           = $null
    DisplayVersion   = $DisplayVersion
    FileVersion      = $FileVersion
    ProductVersion   = $ProductVersion
    BannerVersion    = $bannerVersionText
    Contradictions   = @()
  }
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
$bannerLine = if ($null -ne $bannerText -and $bannerText -match "(?m)^.*Compiler version.*$") { $Matches[0].Trim() } else { "(unparsable or no banner)" }
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
  Write-Host "ci-installer: iscc $iss (via $realIscc)"
  $compileLog = & $realIscc $iss 2>&1 | Out-String
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