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
$rawIsccHelp = & $isccPath /? 2>&1 | Out-String
$fileVersionText = $null
$bannerVersionText = $null
if (Test-Path -LiteralPath $isccPath) {
  $fileVersionText = (Get-Item -LiteralPath $isccPath).VersionInfo.FileVersion
}
if ($rawIsccHelp -match "(?i)Inno\s+Setup\s+([\d.]+)") {
  $bannerVersionText = $Matches[1]
}
$resolvedInno = $null
foreach ($candidate in @($fileVersionText, $bannerVersionText)) {
  if ([string]::IsNullOrWhiteSpace($candidate)) {
    continue
  }

  try {
    $parsed = [Version]$candidate
    $normalized = [Version]"$($parsed.Major).$($parsed.Minor).$($parsed.Build)"
    $resolvedInno = $normalized
    break
  }
  catch {
    continue
  }
}
if ($null -eq $resolvedInno) {
  throw "ci-installer: could not read Inno Setup compiler version from iscc (path=$isccPath; FileVersion=$fileVersionText; iscc /? output:$rawIsccHelp)"
}
if ($resolvedInno.Major -ne $expectedInno.Major -or $resolvedInno.Minor -ne $expectedInno.Minor -or $resolvedInno.Build -ne $expectedInno.Build) {
  throw "ci-installer: expected Inno Setup $expectedInno on PATH; resolved $resolvedInno (FileVersion=$fileVersionText; banner=$bannerVersionText; raw iscc /?:$rawIsccHelp)"
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