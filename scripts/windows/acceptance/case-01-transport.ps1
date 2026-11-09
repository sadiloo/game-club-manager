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
$receiveBody = ""
if ($chosen -eq "ServerSentEvents" -or $chosen -eq "LongPolling") {
    $receiveJob = Start-Job -ScriptBlock {
        param($url, $deviceId, $deviceKey)
        $headers = @{ "X-Device-Id" = $deviceId; "X-Device-Key" = $deviceKey; Accept = "text/event-stream" }
        return (Invoke-WebRequest -Uri $url -Headers $headers -UseBasicParsing -TimeoutSec 30).Content
    } -ArgumentList $startUrl, $deviceId, $deviceKey
    Start-Sleep -Seconds 1
    $payload = '{"protocol":"json","version":1}' + [char]0x1e
    $post = Invoke-WebRequest -Uri $startUrl -Method Post -Body $payload -ContentType "application/json; charset=utf-8" -Headers $headers -UseBasicParsing
    $hubConnected = $post.StatusCode -eq 200
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        if ((Receive-Job $receiveJob -Keep) -match '\{\}') {
            $receiveBody = (Receive-Job $receiveJob)
            $handshakeOk = $true
            break
        }
        Start-Sleep -Milliseconds 200
    }
    Stop-Job $receiveJob -ErrorAction SilentlyContinue | Out-Null
    Remove-Job $receiveJob -Force -ErrorAction SilentlyContinue | Out-Null
}
@{
    case          = 1
    pass          = ($hubConnected -and $handshakeOk)
    server        = $server
    transport     = ($transports -join ",")
    chosen        = $chosen
    hubConnected  = $hubConnected
    handshakeOk   = $handshakeOk
    receiveSample = $receiveBody.Substring(0, [Math]::Min(120, $receiveBody.Length))
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 5 | Set-Content $out
if (-not $hubConnected -or -not $handshakeOk) { exit 1 }
Write-Host "Wrote $out"
