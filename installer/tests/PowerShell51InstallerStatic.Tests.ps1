$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$toolRoots = @(
    (Join-Path $root "installer/tools")
)
$patterns = @(
    @{ Name = "utf8NoBOM encoding"; Regex = '-Encoding\s+utf8NoBOM' },
    @{ Name = "null-coalescing ??"; Regex = '\?\?' },
    @{ Name = "null-conditional ?."; Regex = '\?\.' },
    @{ Name = "ForEach-Object -Parallel"; Regex = 'ForEach-Object\s+-Parallel' },
)
$files = foreach ($dir in $toolRoots) {
    Get-ChildItem -LiteralPath $dir -Recurse -Include *.ps1, *.psm1 -File
}
foreach ($file in $files) {
    $raw = Get-Content -LiteralPath $file.FullName -Raw
    foreach ($p in $patterns) {
        if ($raw -match $p.Regex) {
            throw "PS5.1 incompatibility ($($p.Name)) in $($file.FullName)"
        }
    }
}
Write-Host "PowerShell51InstallerStatic: ok ($($files.Count) files)"