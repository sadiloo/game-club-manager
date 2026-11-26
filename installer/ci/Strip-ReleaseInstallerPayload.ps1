param(
    [Parameter(Mandatory)][string] $ToolsDir
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $ToolsDir)) {
    throw "Strip-ReleaseInstallerPayload: tools dir missing: $ToolsDir"
}

$regionPattern = '(?ms)#region CHECKPOINT_INTERNAL_TEST_HOOKS_BEGIN.*?#endregion CHECKPOINT_INTERNAL_TEST_HOOKS_END\r?\n?'

foreach ($name in @("server-postinstall.ps1", "kiosk-postinstall.ps1")) {
    $path = Join-Path $ToolsDir $name
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $raw = Get-Content -LiteralPath $path -Raw
    $stripped = [regex]::Replace($raw, $regionPattern, '')
    if ($stripped -match 'CHECKPOINT_INTERNAL_TEST_HOOKS') {
        throw "Strip-ReleaseInstallerPayload: $name still references CHECKPOINT_INTERNAL_TEST_HOOKS after strip"
    }
  if ($stripped -match '\.internal-test-fail-phase') {
        throw "Strip-ReleaseInstallerPayload: $name still references .internal-test-fail-phase after strip"
    }
    $enc = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($path, $stripped, $enc)
}

Write-Host "Strip-ReleaseInstallerPayload: ok dir=$ToolsDir"