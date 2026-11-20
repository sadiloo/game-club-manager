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
$innoVersionLine = & iscc.exe /? 2>&1 | Select-String -Pattern "Compiler engine version: Inno Setup" | Select-Object -First 1
if (-not $innoVersionLine) {
  throw "ci-installer: could not read Inno Setup compiler version from iscc /?"
}
if ($innoVersionLine -notmatch "Inno Setup\s+([\d.]+)") {
  throw "ci-installer: unexpected iscc version line: $innoVersionLine"
}
$innoVersion = [Version]$Matches[1]
if ($innoVersion -ne $expectedInno) {
  throw "ci-installer: expected Inno Setup $expectedInno on PATH; found $innoVersion"
}
Write-Host "ci-installer: Inno $innoVersion; compile server and kiosk iss"
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