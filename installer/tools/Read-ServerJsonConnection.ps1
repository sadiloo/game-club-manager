# Parse ConnectionStrings:Default from server.json for pg tools (not Npgsql connection string format).
param(
    [string] $ServerJsonPath = "C:\ProgramData\Checkpoint\config\server.json"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $ServerJsonPath)) {
    throw "server.json not found: $ServerJsonPath"
}

$json = Get-Content -LiteralPath $ServerJsonPath -Raw | ConvertFrom-Json
$cs = [string]$json.ConnectionStrings.Default
if ([string]::IsNullOrWhiteSpace($cs)) {
    throw "ConnectionStrings:Default missing in server.json"
}

$parts = @{}
foreach ($pair in $cs.Split(';')) {
    $kv = $pair.Split('=', 2)
    if ($kv.Count -eq 2) {
        $parts[$kv[0].Trim()] = $kv[1].Trim()
    }
}

[PSCustomObject]@{
    Host     = if ($parts.Host) { $parts.Host } else { "127.0.0.1" }
    Port     = if ($parts.Port) { [int]$parts.Port } else { 5433 }
    Database = if ($parts.Database) { $parts.Database } else { "checkpoint" }
    Username = if ($parts.Username) { $parts.Username } else { "postgres" }
    Password = $parts.Password
}