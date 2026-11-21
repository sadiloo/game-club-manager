# ISCC must receive /DAppVersion — no silent default in .iss preprocessor.
$ErrorActionPreference = "Stop"
$installerRoot = Split-Path $PSScriptRoot -Parent
$paths = @(
    (Join-Path $installerRoot "server/CheckpointServer.iss"),
    (Join-Path $installerRoot "kiosk/CheckpointKiosk.iss")
)

foreach ($issPath in $paths) {
    $text = Get-Content -LiteralPath $issPath -Raw
    $name = Split-Path $issPath -Leaf
    if ($text -notmatch '(?ms)#ifndef\s+AppVersion\s+#error\s+AppVersion must be passed via /DAppVersion\s+#endif') {
        throw "$name`: missing #ifndef AppVersion / #error / #endif block"
    }
    if ($text -match '#define\s+AppVersion\s+"') {
        throw "$name`: must not #define AppVersion to a literal (use /DAppVersion only)"
    }
}

Write-Host "IssAppVersionStatic: ok"