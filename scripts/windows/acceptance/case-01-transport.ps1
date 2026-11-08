$ErrorActionPreference = "Stop"
$out = "$env:ProgramData\Checkpoint\evidence\case-01.json"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$server = ($env:CHECKPOINT_SERVER_URL).TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($server)) { $server = "http://127.0.0.1:5000" }
$deviceId = $env:CHECKPOINT_DEVICE_ID
$deviceKey = $env:CHECKPOINT_DEVICE_KEY
if (-not $deviceId -or -not $deviceKey) { throw "CHECKPOINT_DEVICE_ID and CHECKPOINT_DEVICE_KEY are required" }
$headers = @{
    "X-Device-Id"  = $deviceId
    "X-Device-Key" = $deviceKey
}
$negotiate = Invoke-RestMethod -Uri "$server/hubs/kiosk/negotiate?negotiateVersion=1" -Method Post -Headers $headers
$connectionId = $negotiate.connectionToken
if (-not $connectionId) { throw "negotiate missing connectionToken" }
$transports = @($negotiate.availableTransports | ForEach-Object { $_.transport })
$chosen = ($transports | Where-Object { $_ -eq "LongPolling" -or $_ -eq "ServerSentEvents" } | Select-Object -First 1)
if (-not $chosen) { $chosen = $transports[0] }
$startUrl = "$server/hubs/kiosk?id=$connectionId"
$hubConnected = $false
$handshakeOk = $false
if ($chosen -eq "ServerSentEvents" -or $chosen -eq "LongPolling") {
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    foreach ($h in $headers.GetEnumerator()) { $session.Headers[$h.Key] = $h.Value }
    $first = Invoke-WebRequest -Uri $startUrl -Headers @{ Accept = "text/event-stream" } -WebSession $session -UseBasicParsing
    $hubConnected = $first.StatusCode -eq 200
    $payload = '{"protocol":"json","version":1}' + [char]0x1e
    $post = Invoke-WebRequest -Uri $startUrl -Method Post -Body $payload -ContentType "application/json; charset=utf-8" -Headers $headers -UseBasicParsing
    $handshakeOk = $post.Content -match '\{\}'
}
@{
    case          = 1
    pass          = ($hubConnected -and $handshakeOk)
    server        = $server
    transport     = ($transports -join ",")
    chosen        = $chosen
    hubConnected  = $hubConnected
    handshakeOk   = $handshakeOk
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 5 | Set-Content $out
if (-not $hubConnected -or -not $handshakeOk) { exit 1 }
Write-Host "Wrote $out"
