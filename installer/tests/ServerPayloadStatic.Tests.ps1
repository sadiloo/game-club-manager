$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$stageServer = Join-Path $root "artifacts/installer/stage-server"
$stageGuard = Join-Path $root "artifacts/installer/stage-guard"

$devAppSettings = Join-Path $stageServer "appsettings.Development.json"
if (Test-Path -LiteralPath $devAppSettings) {
    throw "Release server payload must not contain appsettings.Development.json at $devAppSettings"
}

$hookMarkers = @(
    'CHECKPOINT_INTERNAL_TEST_HOOKS',
    '\.internal-test-fail-phase',
    'TESTFAILPHASE',
    'ApplyInternalTestHooks',
    'CHECKPOINT_TEST_FAIL_PHASE',
    'CHECKPOINT_KIOSK_POSTINSTALL_FAIL',
    '#region CHECKPOINT_INTERNAL_TEST_HOOKS_BEGIN'
)

function Assert-ReleasePayloadNoHooks {
    param([string] $Label, [string] $FilePath)
    if (-not (Test-Path -LiteralPath $FilePath)) {
        throw "ServerPayloadStatic: $Label missing at $FilePath (run ci-installer build first)"
    }
    $raw = Get-Content -LiteralPath $FilePath -Raw
    foreach ($marker in $hookMarkers) {
        if ($raw -match $marker) {
            throw "Release payload $Label contains hook marker: $marker"
        }
    }
}

Assert-ReleasePayloadNoHooks -Label "server-postinstall.ps1" -FilePath (Join-Path $stageServer "tools/server-postinstall.ps1")
Assert-ReleasePayloadNoHooks -Label "kiosk-postinstall.ps1" -FilePath (Join-Path $stageGuard "tools/kiosk-postinstall.ps1")

$serverToolsDir = Join-Path $stageServer "tools"
if (Test-Path -LiteralPath $serverToolsDir) {
    Get-ChildItem -LiteralPath $serverToolsDir -Recurse -Include *.ps1, *.psm1 -File | ForEach-Object {
        $raw = Get-Content -LiteralPath $_.FullName -Raw
        foreach ($marker in $hookMarkers) {
            if ($raw -match $marker) {
                throw "Release staged server tools hook marker '$marker' in $($_.FullName)"
            }
        }
    }
}

Write-Host "ServerPayloadStatic: ok"