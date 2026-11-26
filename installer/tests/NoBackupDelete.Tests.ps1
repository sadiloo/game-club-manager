# Static: backup/restore scripts and installers must never delete dumps, pgdata or backups (DEF-4).
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

function Test-BackupDeletionPsLine([string]$line) {
    if ($line -match 'Env:\s*PGPASSWORD|Remove-Item\s+Env:') { return $false }

    $deletes = $line -match 'Remove-Item\b|\bdel\b|\berase\b|\brm\b|\brmdir\b|\[IO\.File\]::Delete|\[IO\.Directory\]::Delete|\[System\.IO\.Directory\]::Delete|\[System\.IO\.File\]::Delete|\bDirectory\.Delete\b|\bClear-Content\b|\bDelTree\b'
    if (-not $deletes) { return $false }

    return $line -match '(?i)backups|\\pgdata|/pgdata|\.dump|\bpgdata\b|\$dump\b|\$DumpPath\b|\$old\b|\$pre\b|\$pgdata\b|\$f\.FullName|\$ownFailedOutput\b|\$preDump\b'
}

# DEF-17: the ONLY sanctioned delete is pg_dump's own failed output, inside Invoke-CheckpointPgDumpNewFile in
# CheckpointDbConfig.ps1 (the target is proven new before pg_dump runs). The same line anywhere else is a hit.
$script:SanctionedDumpDeleteLine = 'Remove-Item -LiteralPath $ownFailedOutput -Force'
function Test-SanctionedOwnFailedDumpDelete([string]$fileName, [string]$functionName, [string]$line) {
    return ($fileName -eq 'CheckpointDbConfig.ps1') -and ($functionName -eq 'Invoke-CheckpointPgDumpNewFile') -and ($line.Trim() -eq $script:SanctionedDumpDeleteLine)
}

function Test-InnoDeleteEntryLine([string]$line) {
    if ($line -notmatch '(?i)(backups|\\pgdata|/pgdata|\.dump|\bpgdata\b)') { return $false }
  return $line -match 'Type:\s*\w+.*Name:\s*"[^"]*"' -or $line -match 'Name:\s*"[^"]*"'
}

function Find-BackupDeletionHits {
    param([string[]]$ScanPaths)
    $hits = @()
    foreach ($scanPath in $ScanPaths) {
        if (-not (Test-Path -LiteralPath $scanPath)) { continue }
        if ((Get-Item -LiteralPath $scanPath).PSIsContainer) {
            $files = Get-ChildItem -Path $scanPath -Include *.ps1, *.psm1 -Recurse -File -ErrorAction SilentlyContinue
        } else {
            $files = @(Get-Item -LiteralPath $scanPath)
        }
        foreach ($file in $files) {
            $lineNo = 0
            $fn = $null
            foreach ($line in (Get-Content -LiteralPath $file.FullName)) {
                $lineNo++
                if ($line -match '^\s*function\s+([\w-]+)') { $fn = $Matches[1] }
                if ($file.Extension -in '.ps1', '.psm1' -and (Test-BackupDeletionPsLine $line)) {
                    if (Test-SanctionedOwnFailedDumpDelete -fileName $file.Name -functionName $fn -line $line) { continue }
                    $hits += "$($file.FullName):$lineNo"
                }
            }
        }
    }
    return $hits
}

function Find-InnoDeleteSectionHits {
    param([object[]]$IssFiles)
    $hits = @()
    foreach ($item in $IssFiles) {
        $path = if ($item -is [string]) { $item } else { $item.FullName }
        if ([string]::IsNullOrWhiteSpace($path)) { continue }
        $section = $null
        $lineNo = 0
        foreach ($line in (Get-Content -LiteralPath $path)) {
            $lineNo++
            if ($line -match '^\[([^\]]+)\]\s*$') {
                $section = $Matches[1]
                continue
            }
            if ($section -notin 'UninstallDelete', 'InstallDelete') { continue }
            $trim = $line.Trim()
            if ($trim.Length -eq 0 -or $trim.StartsWith(';')) { continue }
            if (Test-InnoDeleteEntryLine $trim) {
                $hits += "${path}:$lineNo"
            }
        }
    }
    return $hits
}

$hits = Find-BackupDeletionHits @(
    (Join-Path $root "installer\tools"),
    (Join-Path $root "scripts")
)
$issFiles = Get-ChildItem -Path (Join-Path $root "installer") -Filter *.iss -Recurse |
    Where-Object { $_.FullName -notmatch '\\tests\\' }
$hits += Find-InnoDeleteSectionHits -IssFiles $issFiles
if ($hits.Count -gt 0) {
    Write-Error ("Forbidden backup deletion in: " + ($hits -join ", "))
}

$negativeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("gcm-nbd-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $negativeDir | Out-Null
$negatives = @{
    "RemoveItemDumpVar.ps1"     = 'Remove-Item $dump'
    "RemoveItemFullName.ps1"    = 'Get-ChildItem C:\ProgramData\Checkpoint\backups\*.dump | ForEach-Object { Remove-Item $f.FullName }'
    "RemoveItemDumpPath.ps1"    = 'Remove-Item C:\ProgramData\Checkpoint\backups\old.dump'
    "PipeRemove.ps1"            = 'Get-ChildItem C:\b\backups\*.dump | Remove-Item'
    "IoDelete.ps1"              = '[IO.File]::Delete($dump)'
    "IoFileDelete.ps1"          = '[System.IO.File]::Delete("C:\ProgramData\Checkpoint\backups\x.dump")'
    "Rm.ps1"                    = 'rm $dump'
    "LiteralPathOld.ps1"        = 'Remove-Item -LiteralPath $old.FullName'
    "IoDirDeletePgdata.ps1"     = '[System.IO.Directory]::Delete($pgdata, $true)'
    "DelCmd.ps1"                = 'del C:\ProgramData\Checkpoint\backups\x.dump'
    "EraseCmd.ps1"              = 'erase C:\ProgramData\Checkpoint\backups\x.dump'
    "RmdirCmd.ps1"              = 'rmdir C:\ProgramData\Checkpoint\pgdata'
    "ClearContent.ps1"          = 'Clear-Content C:\ProgramData\Checkpoint\backups\x.dump'
    "DelTree.ps1"               = 'DelTree "C:\ProgramData\Checkpoint\backups"'
    "OwnFailedOutsideHelper.ps1" = "function Invoke-SomethingElse {`n    Remove-Item -LiteralPath `$ownFailedOutput -Force`n}"
    "PsmRemoveDump.psm1"        = 'Remove-Item $dump'
    "RemovePreDump.ps1"         = 'Remove-Item -LiteralPath $preDump -Force'
    "UninstallDelete.iss"       = @'
[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\Checkpoint\backups"
'@
    "InstallDelete.iss"         = @'
[InstallDelete]
Type: filesandordirs; Name: "{commonappdata}\Checkpoint\pgdata"
'@
}
try {
    foreach ($kv in $negatives.GetEnumerator()) {
        $filePath = Join-Path $negativeDir $kv.Key
        Set-Content -LiteralPath $filePath -Value $kv.Value -Encoding UTF8
        if ($filePath -match '\.iss$') {
            $scanHits = Find-InnoDeleteSectionHits -IssFiles @((Get-Item -LiteralPath $filePath))
        } else {
            $scanHits = Find-BackupDeletionHits @($negativeDir)
            $scanHits = $scanHits | Where-Object { $_ -like "$filePath*" }
        }
        if ($scanHits.Count -eq 0) {
            Write-Error "negative control scan did not detect forbidden line: $($kv.Key) => $($kv.Value)"
        }
    }
}
finally {
    Remove-Item -LiteralPath $negativeDir -Recurse -Force -ErrorAction SilentlyContinue
}

# The sanctioned delete exists exactly once, inside the helper, after the new-target guard.
$dbConfig = Get-Content -LiteralPath (Join-Path $root "installer\tools\CheckpointDbConfig.ps1") -Raw
$iFn = $dbConfig.IndexOf('function Invoke-CheckpointPgDumpNewFile')
$iGuard = $dbConfig.IndexOf('refusing to write over an existing file', [Math]::Max(0, $iFn))
$iRun = $dbConfig.IndexOf('& $PgDump @PgArgs -f $OutFile', [Math]::Max(0, $iFn))
$iDel = $dbConfig.IndexOf($script:SanctionedDumpDeleteLine)
if ($iFn -lt 0 -or $iGuard -lt $iFn -or $iRun -lt $iGuard -or $iDel -lt $iRun) {
    Write-Error "NoBackupDelete: Invoke-CheckpointPgDumpNewFile must refuse an existing target before pg_dump and delete only afterwards"
}
if (([regex]::Matches($dbConfig, [regex]::Escape($script:SanctionedDumpDeleteLine))).Count -ne 1) {
    Write-Error "NoBackupDelete: the sanctioned own-failed-output delete must appear exactly once"
}

Write-Host "NoBackupDelete: ok"