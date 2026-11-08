$out = "$env:ProgramData\Checkpoint\evidence\case-03-f6.json"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$server = $env:CHECKPOINT_SERVER_URL
if ([string]::IsNullOrWhiteSpace($server)) { $server = "http://127.0.0.1:5000" }
$loginName = $env:CHECKPOINT_F6_USER
if ([string]::IsNullOrWhiteSpace($loginName)) { $loginName = "f6probe" }
$station = $env:CHECKPOINT_STATION_ID
if ([string]::IsNullOrWhiteSpace($station)) { $station = "PC-1" }
$deviceId = $env:CHECKPOINT_DEVICE_ID
$deviceKey = $env:CHECKPOINT_DEVICE_KEY
$headers = @{}
if ($deviceId -and $deviceKey) {
    $headers["X-Device-Id"] = $deviceId
    $headers["X-Device-Key"] = $deviceKey
}
$failures = @()
for ($i = 0; $i -lt 5; $i++) {
    try {
        $body = @{ loginName = $loginName; password = "wrong"; stationId = $station } | ConvertTo-Json
        Invoke-WebRequest -Uri "$server/api/kiosk/auth/login" -Method Post -Body $body -ContentType "application/json" -Headers $headers -UseBasicParsing | Out-Null
        $failures += "unexpected_success_$i"
    } catch {
        $failures += [int]$_.Exception.Response.StatusCode
    }
}
$results = @{
    case          = 3
    server        = $server
    invalidTries  = $failures
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
}
$results | ConvertTo-Json -Depth 5 | Set-Content $out
Write-Host "Wrote $out"
