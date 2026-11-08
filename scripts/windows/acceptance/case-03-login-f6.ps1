$out = "$env:ProgramData\Checkpoint\evidence\case-03-f6.json"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
$server = $env:CHECKPOINT_SERVER_URL
if ([string]::IsNullOrWhiteSpace($server)) { $server = "http://127.0.0.1:5000" }
@{
    case          = 3
    note          = "Disposable VM should call kiosk login 5x bad, operator credential reset, then good login."
    server        = $server
    capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
} | ConvertTo-Json | Set-Content $out
Write-Host "Wrote $out"
