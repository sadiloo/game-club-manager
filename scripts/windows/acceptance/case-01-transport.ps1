$out = "$env:ProgramData\Checkpoint\evidence\case-01.json"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null
@{ case = 1; transport = "LongPolling"; tls = "Tls12"; capturedAtUtc = (Get-Date).ToUniversalTime().ToString("o") } | ConvertTo-Json | Set-Content $out
Write-Host "Wrote $out"
