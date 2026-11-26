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
    # DEF-19: read as UTF-8 (BOM-aware) and write back WITH the UTF-8 BOM. Windows PowerShell 5.1 reads a BOM-less file
    # in the ANSI code page, so the Persian text in these scripts would stop them parsing (every release install: 135).
    $raw = [System.IO.File]::ReadAllText($path, (New-Object System.Text.UTF8Encoding $true))
    $stripped = [regex]::Replace($raw, $regionPattern, '')
    if ($stripped -match 'CHECKPOINT_INTERNAL_TEST_HOOKS') {
        throw "Strip-ReleaseInstallerPayload: $name still references CHECKPOINT_INTERNAL_TEST_HOOKS after strip"
    }
    if ($stripped -match '\.internal-test-fail-phase') {
        throw "Strip-ReleaseInstallerPayload: $name still references .internal-test-fail-phase after strip"
    }
    $enc = New-Object System.Text.UTF8Encoding $true
    [System.IO.File]::WriteAllText($path, $stripped, $enc)
}

Write-Host "Strip-ReleaseInstallerPayload: ok dir=$ToolsDir"