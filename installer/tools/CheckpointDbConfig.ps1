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

function Test-PostgresDumpReadable {
    param([Parameter(Mandatory)][string]$DumpPath)

    if (-not (Test-Path -LiteralPath $DumpPath)) {
        throw "Dump file missing: $DumpPath"
    }

    if ((Get-Item -LiteralPath $DumpPath).Length -eq 0) {
        throw "Dump file is empty: $DumpPath"
    }

    & pg_restore --list $DumpPath 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "pg_restore --list failed for $DumpPath"
    }
}