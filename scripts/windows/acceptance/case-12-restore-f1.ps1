$ErrorActionPreference = "Stop"
$out = "$env:ProgramData\Checkpoint\evidence\case-12-f1.json"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$server = ($env:CHECKPOINT_SERVER_URL).TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($server)) { $server = "http://127.0.0.1:5000" }
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$null = Invoke-RestMethod -Uri "$server/api/operator/auth/login" -Method Post -Body (@{ username = "operator"; pin = "1111" } | ConvertTo-Json) -ContentType "application/json" -WebSession $session
$station = $env:CHECKPOINT_STATION_ID
if ([string]::IsNullOrWhiteSpace($station)) { $station = "PC-1" }
$start = Invoke-RestMethod -Uri "$server/api/operator/stations/$station/start" -Method Post -WebSession $session
$sessionId = $start.sessionId
$deviceId = $env:CHECKPOINT_DEVICE_ID
$deviceKey = $env:CHECKPOINT_DEVICE_KEY
$headers = @{}
if ($deviceId -and $deviceKey) {
    $headers["X-Device-Id"] = $deviceId
    $headers["X-Device-Key"] = $deviceKey
    $hb = @{ sessionId = $sessionId; clientMonotonicMs = 600000 } | ConvertTo-Json
    Invoke-RestMethod -Uri "$server/hubs/kiosk" -Method Post -Headers $headers -ErrorAction SilentlyContinue | Out-Null
}
Invoke-RestMethod -Uri "$server/api/kiosk/devices/events" -Method Post -Headers $headers -Body (@(
    @{
        eventId = [guid]::NewGuid().ToString()
        type    = "offline_play_segment"
        payloadJson = (@{ sessionId = $sessionId; powered = $true; segmentEndUtc = (Get-Date).ToUniversalTime().ToString("o") } | ConvertTo-Json -Compress)
        createdUtc = (Get-Date).ToUniversalTime().ToString("o")
    }
) | ConvertTo-Json -Depth 5) -ContentType "application/json" | Out-Null
Invoke-RestMethod -Uri "$server/api/operator/stations/$station/dead-pc-sweep" -Method Post -WebSession $session -ErrorAction SilentlyContinue | Out-Null
$queue = Invoke-RestMethod -Uri "$server/api/operator/manual-settle-queue" -WebSession $session
$target = @($queue) | Where-Object { $_.reason -eq "powered_offline_after_autoclose" -and $_.sessionId -eq $sessionId } | Select-Object -First 1
$resolveStatus = $null
$timeGross = $null
$pass = $false
if ($target) {
    $before = Invoke-RestMethod -Uri "$server/api/operator/sessions/$sessionId/bill" -WebSession $session
    $resolve = Invoke-WebRequest -Uri "$server/api/operator/sessions/$sessionId/manual-settle" -Method Post -Body '{"endAt":null}' -ContentType "application/json" -WebSession $session -UseBasicParsing
    $resolveStatus = [int]$resolve.StatusCode
    $after = Invoke-RestMethod -Uri "$server/api/operator/sessions/$sessionId/bill" -WebSession $session
    $timeGross = $after.timeGrossToman
    $pass = ($resolveStatus -ge 200 -and $resolveStatus -lt 300) -and ($before.timeGrossToman -eq $after.timeGrossToman)
}
@{
    case          = 12
    pass          = $pass
    sessionId     = $sessionId
    queueCount    = @($queue).Count
    resolveStatus = $resolveStatus
    timeGross     = $timeGross
    server        = $server
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 5 | Set-Content $out
if (-not $pass) { exit 1 }
Write-Host "Wrote $out"
