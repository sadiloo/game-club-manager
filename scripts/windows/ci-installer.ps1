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

function Test-InnoVersionMatches([string]$fileVersion, [Version]$expected) {
  if ([string]::IsNullOrWhiteSpace($fileVersion)) {
    return $false
  }
  try {
    $parsed = [Version]$fileVersion
    return $parsed.Major -eq $expected.Major `
      -and $parsed.Minor -eq $expected.Minor `
      -and $parsed.Build -eq $expected.Build
  }
  catch {
    return $false
  }
}

function Get-IsccFileVersion([string]$isccPath) {
  if (-not (Test-Path -LiteralPath $isccPath)) {
    return $null
  }
  return (Get-Item -LiteralPath $isccPath).VersionInfo.FileVersion
}

function Get-InnoInstallDirFromRegistry {
  $registryKeys = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1"
  )
  foreach ($keyPath in $registryKeys) {
    if (-not (Test-Path -LiteralPath $keyPath)) {
      continue
    }
    $installLocation = (Get-ItemProperty -LiteralPath $keyPath -ErrorAction SilentlyContinue).InstallLocation
    if (-not [string]::IsNullOrWhiteSpace($installLocation)) {
      return NormalizeDir $installLocation
    }
  }
  return $null
}

function Get-InnoCandidateInstallDirs {
  $ordered = [System.Collections.Generic.List[string]]::new()
  $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

  function Add-Candidate([string]$dir) {
    $normalized = NormalizeDir $dir
    if ($null -eq $normalized) {
      return
    }
    if ($seen.Add($normalized)) {
      $ordered.Add($normalized)
    }
  }

  $regDir = Get-InnoInstallDirFromRegistry
  if ($null -ne $regDir) {
    Add-Candidate $regDir
  }

  Add-Candidate "C:\ProgramData\chocolatey\lib\innosetup\tools"

  $pf86 = ${env:ProgramFiles(x86)}
  if (-not [string]::IsNullOrWhiteSpace($pf86)) {
    Add-Candidate (Join-Path $pf86 "Inno Setup 6")
  }

  return $ordered
}

$pathIsccShim = Get-Command iscc.exe -ErrorAction SilentlyContinue
$pathIsccResolved = if ($null -ne $pathIsccShim) { $pathIsccShim.Source } else { "(not on PATH)" }

$candidateDirs = Get-InnoCandidateInstallDirs
$attemptLog = [System.Collections.Generic.List[string]]::new()
$realIscc = $null
$peFileVersion = $null

foreach ($dir in $candidateDirs) {
  $isccCandidate = Join-Path $dir "ISCC.exe"
  if (-not (Test-Path -LiteralPath $isccCandidate)) {
    $attemptLog.Add("$isccCandidate=(missing)")
    continue
  }
  $ver = Get-IsccFileVersion $isccCandidate
  $attemptLog.Add("$isccCandidate fileVersion=$ver")
  if (Test-InnoVersionMatches $ver $expectedInno) {
    $realIscc = $isccCandidate
    $peFileVersion = $ver
    break
  }
}

if ($null -eq $realIscc) {
  $attemptText = if ($attemptLog.Count -gt 0) { $attemptLog -join "; " } else { "(no candidates)" }
  Write-Error @"
ci-installer: Inno Setup $expectedInno required (real ISCC.exe, never Chocolatey PATH shim).
PATH iscc.exe (ignored if Chocolatey shim)=$pathIsccResolved
attempted paths and versions: $attemptText
"@
  exit 1
}

$realInnoDir = NormalizeDir (Split-Path -Parent $realIscc)
Write-Host "ci-installer: resolved ISCC path=$realIscc fileVersion=$peFileVersion installDir=$realInnoDir"
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