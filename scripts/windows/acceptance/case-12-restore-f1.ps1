$out = "$env:ProgramData\Checkpoint\evidence\case-12-f1.json"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$server = $env:CHECKPOINT_SERVER_URL
if ([string]::IsNullOrWhiteSpace($server)) { $server = "http://127.0.0.1:5000" }
try {
    $queue = Invoke-RestMethod -Uri "$server/api/operator/manual-settle-queue" -Headers @{ "X-Api-Key" = $env:GAMECLUB_API_KEY } -Method Get
} catch {
    $queue = @()
}
@{
    case          = 12
    queueRows     = @($queue).Count
    server        = $server
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json | Set-Content $out
Write-Host "Wrote $out"
