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

@{
    case          = 1
    server        = $server
    httpStatus    = if ($ping) { [int]$ping.StatusCode } else { 0 }
    tls           = [Enum]::GetName([System.Security.Authentication.SslProtocols], [Net.ServicePointManager]::SecurityProtocol)
    transport     = "LongPolling"
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json | Set-Content $out
Write-Host "Wrote $out"
