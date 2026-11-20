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

if (Get-Command iscc.exe -ErrorAction SilentlyContinue) {
  Write-Host "ci-installer: Inno found; compile server iss"
  iscc.exe installer/server/CheckpointServer.iss
  iscc.exe installer/kiosk/CheckpointKiosk.iss
  Get-ChildItem installer -Recurse -Filter *.exe | ForEach-Object {
    Write-Host ("ci-installer: artifact {0} bytes={1}" -f $_.Name, $_.Length)
  }
} else {
  Write-Host "ci-installer: iscc.exe not on PATH; iss compile skipped (sizes still reported)"
}

Write-Host "ci-installer: done"