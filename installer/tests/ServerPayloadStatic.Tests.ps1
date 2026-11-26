$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ci = Get-Content -LiteralPath (Join-Path $root "scripts/windows/ci-installer.ps1") -Raw
if ($ci -notmatch "appsettings\.Development\.json") {
    throw "ci-installer.ps1 must strip appsettings.Development.json from server payload"
}

$post = Get-Content -LiteralPath (Join-Path $root "installer/tools/server-postinstall.ps1") -Raw
if ($post -match '\.internal-test-fail-phase' -and $post -notmatch 'CHECKPOINT_INTERNAL_TEST_HOOKS') {
    throw "server-postinstall: .internal-test-fail-phase must be gated by CHECKPOINT_INTERNAL_TEST_HOOKS"
}

Write-Host "ServerPayloadStatic: ok"