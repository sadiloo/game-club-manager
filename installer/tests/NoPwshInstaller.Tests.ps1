$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$paths = @(
    (Join-Path $root "installer")
)

$offenders = @()
foreach ($path in $paths) {
    if (-not (Test-Path -LiteralPath $path)) { continue }
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $files = @(Get-Item -LiteralPath $path)
    } else {
        $files = Get-ChildItem -Path $path -Recurse -File -Include *.iss, *.ps1, *.psm1
    }
    foreach ($f in $files) {
        if ($f.Name -ieq 'NoPwshInstaller.Tests.ps1') { continue }
        if (Select-String -LiteralPath $f.FullName -Pattern '\bpwsh\b' -Quiet) {
            $offenders += $f.FullName
        }
    }
}

if ($offenders.Count -gt 0) {
    foreach ($o in $offenders) { Write-Error "pwsh is not allowed in installer scripts: $o" }
}

Write-Host "NoPwshInstaller: ok"