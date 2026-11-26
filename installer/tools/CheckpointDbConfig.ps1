# Parse Checkpoint server.json ConnectionStrings:Default into pg/libpq arguments (DEF-5/6).
function Read-CheckpointServerJsonConfig {
    param(
        [Parameter(Mandatory)][string]$ServerJsonPath
    )

    if (-not (Test-Path -LiteralPath $ServerJsonPath)) {
        throw "server.json not found: $ServerJsonPath"
    }

    $json = Get-Content -LiteralPath $ServerJsonPath -Raw | ConvertFrom-Json
    $raw = [string]$json.ConnectionStrings.Default
    if ([string]::IsNullOrWhiteSpace($raw)) {
        throw "ConnectionStrings:Default is missing in $ServerJsonPath"
    }

    $map = @{}
    foreach ($part in $raw.Split(';')) {
        if ([string]::IsNullOrWhiteSpace($part)) { continue }
        $kv = $part.Split('=', 2)
        if ($kv.Count -lt 2) { continue }
        $key = $kv[0].Trim()
        $val = $kv[1].Trim()
        switch -Regex ($key) {
            '^Host$' { $map.Host = $val }
            '^Port$' { $map.Port = $val }
            '^Database$' { $map.Database = $val }
            '^Username$' { $map.Username = $val }
            '^Password$' { $map.Password = $val }
            '^User Id$' { if (-not $map.Username) { $map.Username = $val } }
            '^User ID$' { if (-not $map.Username) { $map.Username = $val } }
        }
    }

    foreach ($required in @('Host', 'Port', 'Database', 'Username', 'Password')) {
        if ([string]::IsNullOrWhiteSpace($map[$required])) {
            throw "ConnectionStrings:Default is missing $required (Npgsql keys; never pass the raw string to pg_restore)."
        }
    }

    return [pscustomobject]$map
}

function Get-CheckpointPgExecutable {
    param(
        [Parameter(Mandatory)][string]$Name
    )

    $installed = Join-Path "C:\Program Files\Checkpoint\PostgreSQL" "bin\$Name.exe"
    if (Test-Path -LiteralPath $installed) {
        return $installed
    }

    return $Name
}

function Test-PostgresDumpReadable {
    param([Parameter(Mandatory)][string]$DumpPath)

    if (-not (Test-Path -LiteralPath $DumpPath)) {
        throw "Dump file missing: $DumpPath"
    }

    if ((Get-Item -LiteralPath $DumpPath).Length -eq 0) {
        throw "Dump file is empty: $DumpPath"
    }

    $pgRestore = Get-CheckpointPgExecutable -Name "pg_restore"
    & $pgRestore --list $DumpPath 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore --list failed for $DumpPath"
    }
}
function Get-CheckpointNewDumpPath {
    # A dump name that does not exist yet, so a new dump never overwrites a previous one (DEF-17).
    param(
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string]$Stem
    )
    $candidate = Join-Path $Directory "$Stem.dump"
    $n = 2
    while (Test-Path -LiteralPath $candidate) {
        $candidate = Join-Path $Directory "$Stem-$n.dump"
        $n++
    }
    return $candidate
}

function Invoke-CheckpointPgDumpNewFile {
    # Runs pg_dump into a file that must NOT exist yet. If pg_dump fails (non-zero exit) or leaves a 0-byte file,
    # the partial/empty file THIS call created is removed and the failure is logged (DEF-17). Existing dumps are
    # never touched: the target is checked to be new before pg_dump starts.
    # -VerifyReadable (R10g DEF-23, ruling: an unreadable dump is a failed dump under PO-3/PO-G): pg_dump exit 0 with
    # bytes that `pg_restore --list` cannot read is a failure too, and that same-run output is removed and logged.
    param(
        [Parameter(Mandatory)][string]$PgDump,
        [Parameter(Mandatory)][string[]]$PgArgs,
        [Parameter(Mandatory)][string]$OutFile,
        [string]$Label = "pg_dump",
        [scriptblock]$Log = { param([string]$line) Write-Host $line },
        [switch]$VerifyReadable
    )
    if (Test-Path -LiteralPath $OutFile) {
        throw "${Label}: refusing to write over an existing file $OutFile"
    }
    & $PgDump @PgArgs -f $OutFile | Out-Host
    $code = $LASTEXITCODE
    $exists = Test-Path -LiteralPath $OutFile
    $bytes = if ($exists) { (Get-Item -LiteralPath $OutFile).Length } else { -1 }
    $reason = $null
    $detail = $null
    if ($code -ne 0) {
        $reason = "exit=$code"
    } elseif ($bytes -le 0) {
        $reason = "empty_output"
    } elseif ($VerifyReadable) {
        try {
            Test-PostgresDumpReadable -DumpPath $OutFile
        } catch {
            $reason = "unreadable"
            $detail = $_.Exception.Message
        }
    }
    if (-not $reason) {
        return [pscustomobject]@{ Ok = $true; ExitCode = 0; Empty = $false; Unreadable = $false; Detail = $null; OutFile = $OutFile }
    }
    if ($exists) {
        $ownFailedOutput = $OutFile
        Remove-Item -LiteralPath $ownFailedOutput -Force
        & $Log "CHECKPOINT_PGDUMP_FAILED label=$Label $reason output=$OutFile bytes=$bytes action=removed_own_failed_output"
    } else {
        & $Log "CHECKPOINT_PGDUMP_FAILED label=$Label $reason output=$OutFile action=no_output_written"
    }
    return [pscustomobject]@{ Ok = $false; ExitCode = $code; Empty = ($reason -eq "empty_output"); Unreadable = ($reason -eq "unreadable"); Detail = $detail; OutFile = $OutFile }
}
