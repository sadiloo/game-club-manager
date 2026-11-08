$out = "$env:ProgramData\Checkpoint\evidence\case-01.json"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$server = $env:CHECKPOINT_SERVER_URL
if ([string]::IsNullOrWhiteSpace($server)) { $server = "http://127.0.0.1:5000" }
$ping = $null
try {
    $ping = Invoke-WebRequest -Uri "$server/health" -UseBasicParsing -TimeoutSec 5
} catch {
    $ping = $_.Exception.Response
}
$negotiate = $null
$transport = "unavailable"
try {
    $negotiate = Invoke-RestMethod -Uri "$server/hubs/kiosk/negotiate?negotiateVersion=1" -Method Post
    if ($negotiate.availableTransports) {
        $transport = ($negotiate.availableTransports | ForEach-Object { $_.transport }) -join ","
    }
} catch {
    $transport = $_.Exception.Message
}
@{
    case          = 1
    server        = $server
    httpStatus    = if ($ping) { [int]$ping.StatusCode } else { 0 }
    tls           = [Enum]::GetName([System.Security.Authentication.SslProtocols], [Net.ServicePointManager]::SecurityProtocol)
    transport     = $transport
    negotiate     = $negotiate
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 5 | Set-Content $out
Write-Host "Wrote $out"
