param(
    [string] $AccountsFile
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
Write-Host "B-8 Windows: restart billing parity via live services"
Clear-InstallerDotNetTestProcessEnvironment
Assert-InstallerServerServicesRunning
Write-AcceptanceHarnessTrace "B-8 restore live CheckpointServer after B-6 dotnet" {
    Restart-Service CheckpointServer -Force
    Wait-InstallerServerApiPort
    Assert-InstallerServerHealthOk
} | Out-Null

$acc = Get-Content -LiteralPath $AccountsFile -Raw | ConvertFrom-Json
$opSession = New-InstallerOperatorClient -Username $acc.manager1User -Pin $acc.manager1Pin

Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/stations" -Body @{ id = "PS5-B8"; name = "PS5-B8"; groupId = "ps5" } | Out-Null
Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/stations" -Body @{ id = "PC-B8"; name = "PC-B8"; groupId = "pc-normal" } | Out-Null
# R10k: the PS5 cap is the seeded rate in force while the session runs, not the day rate (see below).
$ps5StartedUtc = [DateTime]::UtcNow
$ps5 = Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions" -Body @{ stationId = "PS5-B8"; playerCount = 1 }
$pc = Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions" -Body @{ stationId = "PC-B8"; playerCount = 1 }
$ps5Id = ($ps5.Content | ConvertFrom-Json).id
$pcId = ($pc.Content | ConvertFrom-Json).id

Stop-Service CheckpointServer -Force
Start-Sleep -Seconds 2
Start-Service CheckpointServer -ErrorAction Stop
Start-Sleep -Seconds 5
# R10h static re-walk: wait for the API port instead of assuming 5 s is enough after the restart.
Wait-InstallerServerApiPort

$ps5Bill = Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions/$ps5Id/stop" -Body @{}
$ps5StoppedUtc = [DateTime]::UtcNow
$pcBill = Invoke-InstallerApiJson -Method Post -Uri "http://127.0.0.1:5000/api/sessions/$pcId/stop" -Body @{}
$ps5Json = $ps5Bill.Content | ConvertFrom-Json
$pcJson = $pcBill.Content | ConvertFrom-Json
if ($null -eq $ps5Json.unroundedPayableToman) { throw "B-8: PS5 bill missing" }
if ($null -eq $pcJson.unroundedPayableToman) { throw "B-8: PC bill missing" }
# Product question open (tech-lead ruling d): pins *current* behaviour only — PC offline payable is 0 today, not a locked spec rule.
if ([int]$pcJson.unroundedPayableToman -ne 0) { throw "B-8: PC offline bill current behaviour is 0; got $($pcJson.unroundedPayableToman)" }
if ([int]$ps5Json.unroundedPayableToman -lt 1) { throw "B-8: PS5 bill must be positive; got $($ps5Json.unroundedPayableToman)" }
$headers = Get-InstallerApiHeaders
$rates = Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:5000/api/rates" -Headers $headers
# R10k: seeded PS5 special hours are 00:00-08:30 Tehran (GameTimerReference.SpecialStart/SpecialEnd; Tehran is UTC+03:30,
# no DST). Use the seeded line for the band in force at session start and stop; if the session crossed 00:00 or 08:30 the
# cap is the higher of those two lines. A day-only cap was a time-of-day assumption (installer job 140168674648 ran 00:46).
function Test-B8SeededSpecialBand([datetime] $utc) {
    $tod = $utc.AddHours(3.5).TimeOfDay
    return ($tod -ge [TimeSpan]::Zero -and $tod -lt [TimeSpan]::new(8, 30, 0))
}
$ps5Bands = @((Test-B8SeededSpecialBand $ps5StartedUtc), (Test-B8SeededSpecialBand $ps5StoppedUtc)) | Select-Object -Unique
$maxReasonable = 0
foreach ($special in $ps5Bands) {
    $ps5Line = @($rates.lines) | Where-Object { $_.groupId -eq "ps5" -and $_.playMode -eq "Offline" -and $_.playerCount -eq 1 -and ([bool]$_.isSpecialBand -eq $special) } | Select-Object -First 1
    if (-not $ps5Line) { throw "B-8: ps5 rate line missing (special=$special)" }
    if ([int]$ps5Line.hourlyToman -gt $maxReasonable) { $maxReasonable = [int]$ps5Line.hourlyToman }
}
Write-Host "B-8: ps5 cap band special=$($ps5Bands -join ',') start_tehran=$($ps5StartedUtc.AddHours(3.5).ToString('HH:mm:ss')) stop_tehran=$($ps5StoppedUtc.AddHours(3.5).ToString('HH:mm:ss'))"
if ([int]$ps5Json.unroundedPayableToman -gt $maxReasonable) {
    throw "B-8: PS5 bill $($ps5Json.unroundedPayableToman) exceeds 1h offline cap $maxReasonable"
}
Write-Host "B-8: ok ps5=$($ps5Json.unroundedPayableToman) pc=$($pcJson.unroundedPayableToman) cap=$maxReasonable"