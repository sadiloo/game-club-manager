# Enumerate current LAN IPv4 addresses on up, non-loopback adapters (server installer finish page + CI).
param(
    [string] $ServerJsonPath = "$env:ProgramData\Checkpoint\config\server.json",
    [ValidateSet("ips", "verbose")]
    [string] $Format = "ips"
)

$ErrorActionPreference = "Stop"

function Get-ServerListenPort {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return 5000 }
    try {
        $json = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        $urls = [string]$json.Urls
        if ($urls -match ':(\d+)\s*$' -or $urls -match ':(\d+)') {
            return [int]$Matches[1]
        }
    } catch { }
    return 5000
}

$port = Get-ServerListenPort -Path $ServerJsonPath
$ips = [System.Collections.Generic.List[string]]::new()
if (-not (Get-Command Get-NetIPAddress -ErrorAction SilentlyContinue)) {
    if ($Format -eq "verbose") {
        Write-Output "PORT=$port"
        exit 0
    }
    exit 0
}
foreach ($addr in Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue) {
    if ($addr.IPAddress -like '127.*' -or $addr.IPAddress -like '169.254.*') { continue }
    $adapter = Get-NetAdapter -InterfaceIndex $addr.InterfaceIndex -ErrorAction SilentlyContinue
    if ($null -eq $adapter) { continue }
    if ($adapter.Status -ne 'Up') { continue }
    if (-not $ips.Contains($addr.IPAddress)) {
        $ips.Add($addr.IPAddress)
    }
}
$ips.Sort()

if ($Format -eq "verbose") {
    Write-Output "PORT=$port"
    foreach ($ip in $ips) { Write-Output "IP=$ip" }
    exit 0
}

foreach ($ip in $ips) { Write-Output $ip }