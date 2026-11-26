# DEF-19: the STAGED payload (what Setup ships), not the source, must be readable by Windows PowerShell 5.1:
# every .ps1/.psm1 has a UTF-8 BOM or is pure ASCII, and parses as 5.1 decodes it. ci-installer.ps1 also runs the
# checker under powershell.exe (real 5.1 loader) for the release and the hooks payload.
$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$checker = Join-Path $root "installer/ci/Test-StagedPayloadEncoding.ps1"
$dirs = @((Join-Path $root "artifacts/installer/stage-server/tools"), (Join-Path $root "artifacts/installer/stage-guard/tools"))
foreach ($d in $dirs) { if (-not (Test-Path -LiteralPath $d)) { throw "StagedPayloadEncoding: staged dir missing: $d (run ci-installer staging first)" } }

# Negative control first: a BOM-less copy of the staged server-postinstall.ps1 must be refused.
$neg = Join-Path ([System.IO.Path]::GetTempPath()) ("cp-def19-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $neg | Out-Null
try {
    $text = [System.IO.File]::ReadAllText((Join-Path $dirs[0] "server-postinstall.ps1"), (New-Object System.Text.UTF8Encoding $true))
    if ($text -notmatch '[^\x00-\x7F]') { throw "StagedPayloadEncoding: negative control needs non-ASCII text in server-postinstall.ps1" }
    [System.IO.File]::WriteAllText((Join-Path $neg "server-postinstall.ps1"), $text, (New-Object System.Text.UTF8Encoding $false))
    $threw = $false
    $negOut = New-Object System.Collections.Generic.List[string]
    try { & $checker -Dirs @($neg) *>&1 | ForEach-Object { $negOut.Add("$_") } } catch { $threw = $true; $negOut.Add("$_") }
    if (-not $threw) { throw "StagedPayloadEncoding: negative control (BOM-less server-postinstall.ps1) was NOT refused by the checker" }
    # The refusal must include the 5.1 parse failure itself (ANSI decode / powershell.exe loader), not only the BOM rule.
    $parseHits = @($negOut | Where-Object { $_ -match 'FAIL .*server-postinstall\.ps1 \[(ansi-|powershell\.exe loader)' })
    if ($parseHits.Count -eq 0) { throw "StagedPayloadEncoding: negative control was refused without a 5.1 parse error: $($negOut -join ' | ')" }
} finally {
    Remove-Item -LiteralPath $neg -Recurse -Force -ErrorAction SilentlyContinue
}
# Second negative control: BOM-less with non-ASCII ONLY in a comment (parses fine) must still be refused (BOM rule).
$neg2 = Join-Path ([System.IO.Path]::GetTempPath()) ("cp-def19b-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $neg2 | Out-Null
try {
    $commentOnly = "# " + [string][char]0x0645 + [char]0x062A + [char]0x0646 + "`nWrite-Host 'x'`n"
    [System.IO.File]::WriteAllText((Join-Path $neg2 "comment-only.ps1"), $commentOnly, (New-Object System.Text.UTF8Encoding $false))
    $threw = $false
    try { & $checker -Dirs @($neg2) *>&1 | Out-Null } catch { $threw = $true }
    if (-not $threw) { throw "StagedPayloadEncoding: negative control (BOM-less, non-ASCII comment only) was NOT refused by the checker" }
} finally {
    Remove-Item -LiteralPath $neg2 -Recurse -Force -ErrorAction SilentlyContinue
}

# The real Windows PowerShell 5.1 check in CI must stay wired for both payloads (release + hooks).
$ci = Get-Content -LiteralPath (Join-Path $root "scripts/windows/ci-installer.ps1") -Raw
if ($ci -notmatch "& powershell\.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \`$checker") { throw "StagedPayloadEncoding: ci-installer.ps1 must run the checker under powershell.exe" }
foreach ($mode in @("Release", "Hooks")) {
    if ($ci -notmatch ("Sync-InstallerStagedTools -Mode $mode\r?\nInvoke-StagedPayloadPs51Check -Label `"" + $mode.ToLowerInvariant() + "`"")) {
        throw "StagedPayloadEncoding: ci-installer.ps1 must run Invoke-StagedPayloadPs51Check right after Sync-InstallerStagedTools -Mode $mode"
    }
}

& $checker -Dirs $dirs
Write-Host "StagedPayloadEncoding: ok"
