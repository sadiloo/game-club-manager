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
$hubConnected = $false
$chosen = $null
if ($negotiate -and $negotiate.connectionToken) {
    try {
        $hubUrl = "$server/hubs/kiosk?id=$($negotiate.connectionToken)"
        $ws = New-Object System.Net.WebSockets.ClientWebSocket
        $cts = New-Object System.Threading.CancellationTokenSource
        $cts.CancelAfter(5000)
        $connect = $ws.ConnectAsync([Uri]$hubUrl, $cts.Token)
        $connect.Wait()
        $hubConnected = $ws.State -eq "Open"
        $chosen = "WebSockets"
        $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "probe", $cts.Token).Wait()
    } catch {
        $chosen = "LongPolling"
        try {
            $poll = Invoke-WebRequest -Uri "$server/hubs/kiosk?id=$($negotiate.connectionToken)" -Headers @{ "X-Requested-With" = "XMLHttpRequest" } -UseBasicParsing -TimeoutSec 5
            $hubConnected = $poll.StatusCode -eq 200
        } catch {
            $hubConnected = $false
        }
    }
}
@{
    case          = 1
    server        = $server
    httpStatus    = if ($ping) { [int]$ping.StatusCode } else { 0 }
    tls           = [Enum]::GetName([System.Security.Authentication.SslProtocols], [Net.ServicePointManager]::SecurityProtocol)
    transport     = $transport
    chosen        = $chosen
    hubConnected  = $hubConnected
    negotiate     = $negotiate
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json -Depth 5 | Set-Content $out
Write-Host "Wrote $out"
