param(
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string]$StationId
)
$env:CHECKPOINT_SERVER_URL = $ServerUrl
$env:CHECKPOINT_STATION_ID = $StationId
Start-Process -FilePath ".\GameClub.Kiosk.exe"
