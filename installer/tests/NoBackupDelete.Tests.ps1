# Static: backup/restore scripts and installers must never delete dumps, pgdata or backups (DEF-4).
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

function Test-BackupDeletionLine([string]$line) {
  if ($line -match 'Remove-Item[^\n#]*\$\w*') {
    if ($line -match 'Env:|PGPASSWORD') { return $false }
  }
  if ($line -notmatch 'Remove-Item\b|\bdel\b|\berase\b|\brm\b|\brmdir\b|\[IO\.File\]::Delete|\[IO\.Directory\]::Delete|\bDirectory\.Delete\b|\bClear-Content\b|\[UninstallDelete\]|\[InstallDelete\]|\bDelTree\b') {
    return $false
  }
  return $line -match 'backups?|\\pgdata|\.dump|pgdata'
}

$hits = @()
$scanPaths = @(
  (Join-Path $root "installer\tools"),
  (Join-Path $root "scripts")
)
Get-ChildItem -Path $scanPaths -Filter *.ps1 -Recurse | ForEach-Object {
  $lineNo = 0
  foreach ($line in (Get-Content $_.FullName)) {
    $lineNo++
    if (Test-BackupDeletionLine $line) {
      $hits += "$($_.FullName):$lineNo"
    }
  }
}
Get-ChildItem -Path (Join-Path $root "installer") -Filter *.iss -Recurse | ForEach-Object {
  $lineNo = 0
  foreach ($line in (Get-Content $_.FullName)) {
    $lineNo++
    if (Test-BackupDeletionLine $line) {
      $hits += "$($_.FullName):$lineNo"
    }
  }
}
if ($hits.Count -gt 0) {
  Write-Error ("Forbidden backup deletion in: " + ($hits -join ", "))
}

$negativeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gcm-nbd-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $negativeDir | Out-Null
$negatives = @{
  "Remove-Item.ps1"       = 'Remove-Item C:\ProgramData\Checkpoint\backups\old.dump'
  "PipeRemove.ps1"      = 'Get-ChildItem C:\b\backups\*.dump | Remove-Item'
  "IoDelete.ps1"        = '[IO.File]::Delete("C:\ProgramData\Checkpoint\backups\x.dump")'
  "Rm.ps1"              = 'rm C:\ProgramData\Checkpoint\backups\x.dump'
  "UninstallDelete.iss" = '[UninstallDelete] Type: files; Name: "C:\ProgramData\Checkpoint\backups\old.dump"'
}
try {
  foreach ($kv in $negatives.GetEnumerator()) {
    if (-not (Test-BackupDeletionLine $kv.Value)) {
      Write-Error "negative control did not match pattern: $($kv.Key)"
    }
  }
}
finally {
  Remove-Item -LiteralPath $negativeDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "NoBackupDelete: ok"