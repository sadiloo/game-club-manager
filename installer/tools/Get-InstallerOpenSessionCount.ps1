# §2.2α: query GET /api/local/open-sessions (existing server API). stdout: COUNT=<n>, UNKNOWN, or NOKEY.
param(
    [string] $ServerJsonPath = "C:\ProgramData\Checkpoint\config\server.json",
    [string] $BaseUrl = "http://127.0.0.1:5000"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $ServerJsonPath)) {
    Write-Output "UNKNOWN"
    exit 2
}
$json = Get-Content -LiteralPath $ServerJsonPath -Raw | ConvertFrom-Json
$key = [string]$json.GAMECLUB_API_KEY
if ([string]::IsNullOrWhiteSpace($key)) {
    Write-Output "NOKEY"
    exit 2
}
try {
    $headers = @{ "X-Api-Key" = $key }
    $resp = Invoke-RestMethod -Uri "$BaseUrl/api/local/open-sessions" -Headers $headers -Method Get -TimeoutSec 8
    $count = [int]$resp.count
    if ($resp.PSObject.Properties.Name -contains "openFreeUses") {
        $count += [int]$resp.openFreeUses
    }
    Write-Output ("COUNT=" + $count)
    exit 0
} catch {
    Write-Output "UNKNOWN"
    exit 2
}