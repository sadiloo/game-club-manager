# Static: backup/restore scripts must never delete dumps (B-3).
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$hits = @()
Get-ChildItem -Path (Join-Path $root "installer\tools"), (Join-Path $root "scripts") -Filter *.ps1 -Recurse | ForEach-Object {
  $text = Get-Content $_.FullName -Raw
  if ($text -match 'Remove-Item.*\.dump|del\s+.*\.dump|Delete.*backup') {
    $hits += $_.FullName
  }
}
if ($hits.Count -gt 0) {
  Write-Error ("Forbidden backup deletion in: " + ($hits -join ", "))
}
Write-Host "NoBackupDelete: ok"