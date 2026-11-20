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

function Get-ChocoInnoPackageVersion {
  $libRoot = "C:\ProgramData\chocolatey\lib\innosetup"
  if (Test-Path -LiteralPath $libRoot) {
    $nuspec = Get-ChildItem -LiteralPath $libRoot -Filter "*.nuspec" -Recurse -ErrorAction SilentlyContinue |
      Sort-Object FullName |
      Select-Object -First 1
    if ($null -ne $nuspec) {
      try {
        [xml]$xml = Get-Content -LiteralPath $nuspec.FullName -ErrorAction Stop
        return [Version]([string]$xml.package.metadata.version)
      }
      catch {
        Write-Host "ci-installer: failed to parse nuspec $($nuspec.FullName): $_"
      }
    }
  }

  $choco = Get-Command choco.exe -ErrorAction SilentlyContinue
  if ($null -eq $choco) {
    return $null
  }
  $lines = & $choco.Source list --local-only --limit-output innosetup 2>&1
  foreach ($line in $lines) {
    if ($line -match '^innosetup\|([0-9.]+)') {
      return [Version]$Matches[1]
    }
  }
  return $null
}

function Get-InnoPeFileVersion([string]$installDir) {
  foreach ($name in @("ISCmplr.dll", "Compil32.exe", "ISCC.exe")) {
    $path = Join-Path $installDir $name
    if (-not (Test-Path -LiteralPath $path)) {
      continue
    }
    $ver = (Get-Item -LiteralPath $path).VersionInfo.FileVersion
    if (-not [string]::IsNullOrWhiteSpace($ver)) {
      return $ver
    }
  }
  return $null
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

function Resolve-RealInnoInstallDirectory {
  $candidates = [System.Collections.Generic.List[string]]::new()

  $chocoTools = "C:\ProgramData\chocolatey\lib\innosetup\tools"
  if (Test-Path -LiteralPath $chocoTools) {
    $candidates.Add((NormalizeDir $chocoTools))
  }

  $chocoLib = "C:\ProgramData\chocolatey\lib\innosetup"
  if (Test-Path -LiteralPath $chocoLib) {
    Get-ChildItem -LiteralPath $chocoLib -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
      $dir = NormalizeDir (Split-Path -Parent $_.FullName)
      if ($null -ne $dir) {
        $candidates.Add($dir)
      }
    }
  }

  $pf86 = ${env:ProgramFiles(x86)}
  if (-not [string]::IsNullOrWhiteSpace($pf86)) {
    $inno6 = Join-Path $pf86 "Inno Setup 6"
    if (Test-Path -LiteralPath $inno6) {
      $candidates.Add((NormalizeDir $inno6))
    }
  }

  $pathIscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
  if ($null -ne $pathIscc) {
    $shimDir = NormalizeDir (Split-Path -Parent $pathIscc.Source)
    if ($null -ne $shimDir -and $shimDir -notmatch '(?i)chocolatey\\bin') {
      $candidates.Add($shimDir)
    }
  }

  return $candidates | Sort-Object -Unique
}

$chocoVersion = Get-ChocoInnoPackageVersion
$pathIsccResolved = if (Get-Command iscc.exe -ErrorAction SilentlyContinue) {
  (Get-Command iscc.exe).Source
} else {
  "(not on PATH)"
}

$candidateDirs = Resolve-RealInnoInstallDirectory
$realInnoDir = $null
$peFileVersion = $null
foreach ($dir in $candidateDirs) {
  $isccCandidate = Join-Path $dir "ISCC.exe"
  if (-not (Test-Path -LiteralPath $isccCandidate)) {
    continue
  }
  $ver = Get-InnoPeFileVersion $dir
  if (Test-InnoVersionMatches $ver $expectedInno) {
    $realInnoDir = $dir
    $peFileVersion = $ver
    break
  }
}

$realIscc = if ($null -ne $realInnoDir) { Join-Path $realInnoDir "ISCC.exe" } else { $null }
$versionOk = $null -ne $chocoVersion `
  -and $chocoVersion.Major -eq $expectedInno.Major `
  -and $chocoVersion.Minor -eq $expectedInno.Minor `
  -and $chocoVersion.Build -eq $expectedInno.Build `
  -and $null -ne $realIscc `
  -and (Test-Path -LiteralPath $realIscc) `
  -and (Test-InnoVersionMatches $peFileVersion $expectedInno)

if (-not $versionOk) {
  $candidateText = ($candidateDirs -join "; ")
  if ([string]::IsNullOrWhiteSpace($candidateText)) { $candidateText = "(none)" }
  Write-Error @"
ci-installer: Inno Setup $expectedInno required (real ISCC, not Chocolatey shim only).
choco local innosetup version=$chocoVersion
PATH iscc.exe=$pathIsccResolved
candidate install directories=$candidateText
resolved real ISCC=$realIscc
pe file version=$peFileVersion
"@
  exit 1
}

Write-Host "ci-installer: Inno choco=$chocoVersion pe=$peFileVersion dir=$realInnoDir; compile server and kiosk iss"
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