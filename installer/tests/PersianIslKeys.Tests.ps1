# DEF-7: Persian.isl [Messages] keys must match Inno Setup 6.7.1 Default.isl exactly.
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

function Get-IslMessageKeys([string]$path) {
    $keys = [System.Collections.Generic.List[string]]::new()
    $inMessages = $false
    foreach ($line in (Get-Content -LiteralPath $path)) {
        if ($line.Trim() -eq "[Messages]") {
            $inMessages = $true
            continue
        }
        if ($inMessages -and $line -match '^\[.+\]\s*$') { break }
        if ($inMessages -and $line -match '^\s*;' ) { continue }
        if ($inMessages -and $line -match '^([^=]+)=(.*)$') {
            $keys.Add($Matches[1].Trim())
        }
    }
    return $keys
}

$pinPath = Join-Path $PSScriptRoot "fixtures\InnoSetup-6.7.1-Default-MessageKeys.txt"
if (-not (Test-Path -LiteralPath $pinPath)) {
    Write-Error "Missing pinned key list: $pinPath"
}
$expected = [System.Collections.Generic.HashSet[string]]::new([string[]](Get-Content -LiteralPath $pinPath | ForEach-Object { $_.Trim() } | Where-Object { $_ }))
$persianPath = Join-Path $root "installer\lang\Persian.isl"
$actualKeys = Get-IslMessageKeys -path $persianPath
$actual = [System.Collections.Generic.HashSet[string]]::new([string[]]$actualKeys)

$missing = @($expected | Where-Object { -not $actual.Contains($_) })
$extra = @($actual | Where-Object { -not $expected.Contains($_) })
if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
    $msg = "Persian.isl message keys differ from Inno 6.7.1 Default.isl."
    if ($missing.Count -gt 0) { $msg += " Missing ($($missing.Count)): " + ($missing -join ", ") + "." }
    if ($extra.Count -gt 0) { $msg += " Extra ($($extra.Count)): " + ($extra -join ", ") + "." }
    Write-Error $msg
}

Write-Host "PersianIslKeys: ok ($($actual.Count) keys)"