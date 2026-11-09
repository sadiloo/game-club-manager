$ErrorActionPreference = "Stop"
$out = "$env:ProgramData\Checkpoint\evidence\case-12-f1.json"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$server = ($env:CHECKPOINT_SERVER_URL).TrimEnd('/')
if ([string]::IsNullOrWhiteSpace($server)) { $server = "http://127.0.0.1:5000" }
$apiKey = $env:GAMECLUB_API_KEY
if ([string]::IsNullOrWhiteSpace($apiKey)) { throw "GAMECLUB_API_KEY is required for dead-pc-sweep and checkpoints" }
$gapSeconds = if ($env:CHECKPOINT_CASE12_GAP_SECONDS) { [int]$env:CHECKPOINT_CASE12_GAP_SECONDS } else { 601 }
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$null = Invoke-RestMethod -Uri "$server/api/operator/auth/login" -Method Post -Body (@{ username = "operator"; pin = "1111" } | ConvertTo-Json) -ContentType "application/json" -WebSession $session
$station = $env:CHECKPOINT_STATION_ID
if ([string]::IsNullOrWhiteSpace($station)) { $station = "PC-1" }
$start = Invoke-RestMethod -Uri "$server/api/operator/stations/$station/start" -Method Post -WebSession $session
$sessionId = $start.id
$deviceId = $env:CHECKPOINT_DEVICE_ID
$deviceKey = $env:CHECKPOINT_DEVICE_KEY
$kioskHeaders = @{ "X-Api-Key" = $apiKey }
if ($deviceId -and $deviceKey) {
    $kioskHeaders["X-Device-Id"] = $deviceId
    $kioskHeaders["X-Device-Key"] = $deviceKey
}
$machineHeaders = @{ "X-Api-Key" = $apiKey }
Invoke-RestMethod -Uri "$server/api/sessions/$sessionId/checkpoints" -Method Post -Headers $machineHeaders -ContentType "application/json" -Body '{}' | Out-Null
Start-Sleep -Seconds $gapSeconds
$sweep = Invoke-RestMethod -Uri "$server/api/internal/dead-pc-sweep" -Method Post -Headers $machineHeaders
$eventPayload = @{
    eventId     = [guid]::NewGuid().ToString()
    type        = "offline_play_segment"
    payloadJson = (@{ sessionId = $sessionId; powered = $true; segmentEndUtc = (Get-Date).ToUniversalTime().ToString("o") } | ConvertTo-Json -Compress)
    createdUtc  = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 5
Invoke-RestMethod -Uri "$server/api/kiosk/devices/events" -Method Post -Headers $kioskHeaders -ContentType "application/json" -Body "[$eventPayload]" | Out-Null
$queue = Invoke-RestMethod -Uri "$server/api/operator/manual-settle-queue" -WebSession $session
$target = @($queue) | Where-Object { $_.reason -eq "powered_offline_after_autoclose" -and $_.sessionId -eq $sessionId } | Select-Object -First 1
$resolveStatus = $null
$timeGross = $null
$beforeGross = $null
$pass = $false
if ($target) {
    $before = Invoke-RestMethod -Uri "$server/api/operator/sessions/$sessionId/bill" -WebSession $session
    $beforeGross = $before.timeGrossToman
    $resolve = Invoke-WebRequest -Uri "$server/api/operator/sessions/$sessionId/manual-settle" -Method Post -Body '{"endAt":null}' -ContentType "application/json" -WebSession $session -UseBasicParsing
    $resolveStatus = [int]$resolve.StatusCode
    $after = Invoke-RestMethod -Uri "$server/api/operator/sessions/$sessionId/bill" -WebSession $session
    $timeGross = $after.timeGrossToman
    $pass = ($resolveStatus -ge 200 -and $resolveStatus -lt 300) -and ($beforeGross -eq $after.timeGrossToman)
}
@{
    case          = 12
    pass          = $pass
    sessionId     = $sessionId
    sweepClosed   = $sweep.closed
    queueCount    = @($queue).Count
    resolveStatus = $resolveStatus
  timeGrossBefore = $beforeGross
    timeGross     = $timeGross
    gapSeconds    = $gapSeconds
    server        = $server
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 5 | Set-Content $out
if (-not $pass) { exit 1 }
Write-Host "Wrote $out"
