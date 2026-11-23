$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$iss = Get-Content -LiteralPath (Join-Path $root "installer/server/CheckpointServer.iss") -Raw
$finish = Get-Content -LiteralPath (Join-Path $root "installer/server/ServerFinish.inc.iss") -Raw
$wizard = Get-Content -LiteralPath (Join-Path $root "installer/server/ServerWizard.inc.iss") -Raw
$tools = Join-Path $root "installer/tools/Get-ServerLanIPv4Addresses.ps1"

if (-not (Test-Path -LiteralPath $tools)) {
    throw "Get-ServerLanIPv4Addresses.ps1 missing"
}
if ($iss -notmatch 'inst\.server\.finish\.ipLabel') {
    throw "CheckpointServer.iss missing CustomMessage inst_server_finish_ipLabel"
}
if ($iss -notmatch 'inst\.server\.finish\.dhcpNote') {
    throw "CheckpointServer.iss missing CustomMessage inst_server_finish_dhcpNote"
}
if ($iss -notmatch 'TODO_UIUX inst\.server\.finish\.ipLabel') {
    throw "ipLabel CustomMessage must use TODO_UIUX placeholder"
}
if ($finish -notmatch 'Get-ServerLanIPv4Addresses\.ps1') {
    throw "ServerFinish.inc.iss must call Get-ServerLanIPv4Addresses.ps1"
}
if ($finish -notmatch 'UpdateServerFinishPage') {
    throw "ServerFinish.inc.iss missing UpdateServerFinishPage"
}
if ($wizard -notmatch 'wpFinished' -or $wizard -notmatch 'UpdateServerFinishPage') {
    throw "ServerWizard.inc.iss must call UpdateServerFinishPage on wpFinished"
}

Write-Host "ServerFinishLanStatic: ok"