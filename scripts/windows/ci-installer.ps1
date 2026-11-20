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
if (-not (Get-Command iscc.exe -ErrorAction SilentlyContinue)) {
  throw "ci-installer: iscc.exe not on PATH; Inno Setup required."
}
$isccPath = (Get-Command iscc.exe).Source
$isccDir = [System.IO.Path]::GetFullPath((Split-Path -Parent $isccPath)).TrimEnd('\')
$rawIsccHelp = & $isccPath /? 2>&1 | Out-String

function NormalizeDir([string]$path) {
  if ([string]::IsNullOrWhiteSpace($path)) {
    return $null
  }
  return [System.IO.Path]::GetFullPath($path).TrimEnd('\')
}

$uninstallRoots = @(
  "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
  "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
)
$knownInnoKeySuffixes = @("Inno Setup 6_is1", "{code-Inno Setup 6_is1}")

$registryMatches = [System.Collections.Generic.List[object]]::new()
foreach ($root in $uninstallRoots) {
  if (-not (Test-Path -LiteralPath $root)) {
    continue
  }
  foreach ($suffix in $knownInnoKeySuffixes) {
    $keyPath = Join-Path $root $suffix
    if (-not (Test-Path -LiteralPath $keyPath)) {
      continue
    }
    $props = Get-ItemProperty -LiteralPath $keyPath -ErrorAction SilentlyContinue
    if ($null -ne $props) {
      $registryMatches.Add([PSCustomObject]@{
          RegistryKeyPath = $keyPath
          DisplayVersion  = $props.DisplayVersion
          InstallLocation = $props.InstallLocation
        })
    }
  }

  Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue | ForEach-Object {
    $props = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
    if ($null -eq $props) {
      return
    }
    $name = [string]$props.DisplayName
    if ($name -notmatch "(?i)Inno Setup 6") {
      return
    }
    $registryMatches.Add([PSCustomObject]@{
        RegistryKeyPath = $_.PSPath
        DisplayVersion  = $props.DisplayVersion
        InstallLocation = $props.InstallLocation
      })
  }
}

$deduped = $registryMatches | Sort-Object RegistryKeyPath -Unique
$matchedInstall = $null
foreach ($entry in $deduped) {
  $installDir = NormalizeDir $entry.InstallLocation
  if ($null -ne $installDir -and $installDir -ieq $isccDir) {
    $matchedInstall = $entry
    break
  }
}
if ($null -eq $matchedInstall -and $deduped.Count -eq 1) {
  $only = $deduped[0]
  $installDir = NormalizeDir $only.InstallLocation
  if ($null -ne $installDir -and $installDir -ieq $isccDir) {
    $matchedInstall = $only
  }
}

$registryKeyPath = if ($matchedInstall) { $matchedInstall.RegistryKeyPath } else { "(no matching uninstall key)" }
$displayVersionText = if ($matchedInstall) { [string]$matchedInstall.DisplayVersion } else { "(unset)" }
$installLocationText = if ($matchedInstall) { [string]$matchedInstall.InstallLocation } else { "(unset)" }

$resolvedInno = $null
if (-not [string]::IsNullOrWhiteSpace($displayVersionText) -and $displayVersionText -ne "(unset)") {
  try {
    $parsed = [Version]$displayVersionText
    $resolvedInno = [Version]"$($parsed.Major).$($parsed.Minor).$($parsed.Build)"
  }
  catch {
    $resolvedInno = $null
  }
}

$versionOk = $null -ne $resolvedInno `
  -and $resolvedInno.Major -eq $expectedInno.Major `
  -and $resolvedInno.Minor -eq $expectedInno.Minor `
  -and $resolvedInno.Build -eq $expectedInno.Build
$locationOk = $null -ne $matchedInstall
if (-not $versionOk -or -not $locationOk) {
  Write-Error @"
ci-installer: expected Inno Setup $expectedInno installed at the same directory as iscc on PATH.
registryKeyPath=$registryKeyPath
DisplayVersion=$displayVersionText
InstallLocation=$installLocationText
resolved iscc path=$isccPath
iscc install directory=$isccDir
iscc /? output:
$rawIsccHelp
"@
  exit 1
}
Write-Host "ci-installer: Inno $resolvedInno; compile server and kiosk iss"
$issTargets = @(
  (Join-Path $root "installer/server/CheckpointServer.iss"),
  (Join-Path $root "installer/kiosk/CheckpointKiosk.iss")
)
foreach ($iss in $issTargets) {
  Write-Host "ci-installer: iscc $iss"
  & iscc.exe $iss
  if ($LASTEXITCODE -ne 0) {
    throw "ci-installer: iscc failed for $iss (exit $LASTEXITCODE)"
  }
}
Get-ChildItem installer -Recurse -Filter *.exe | ForEach-Object {
  Write-Host ("ci-installer: artifact {0} bytes={1}" -f $_.Name, $_.Length)
}

Write-Host "ci-installer: done"