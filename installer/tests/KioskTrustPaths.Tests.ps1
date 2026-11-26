$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

$allowFiles = @(
    (Join-Path $root "src/GameClub.Kiosk.Core/Security/KioskTrustFileDirectClient.cs"),
    (Join-Path $root "src/GameClub.Kiosk.Core/Security/KioskTrustFilePipeClient.cs"),
    (Join-Path $root "src/GameClub.Kiosk.Core/Security/KioskTrustFileAtomicWrite.cs")
) | ForEach-Object { (Resolve-Path -LiteralPath $_).Path }

$scanRoots = @(
    (Join-Path $root "src/GameClub.Kiosk.Core"),
    (Join-Path $root "src/GameClub.Kiosk")
)

$patterns = @(
    'WriteAllText\s*\(\s*[^,]*setup\.json',
    'WriteAllText\s*\(\s*[^,]*admin-lock\.json',
    'File\.Write(AllText|AllBytes|AllLines)\s*\([^)]*setup\.json',
    'File\.Write(AllText|AllBytes|AllLines)\s*\([^)]*admin-lock\.json'
)

# Negative control: patterns must detect decoy line.
$decoy = Get-Content -LiteralPath (Join-Path $PSScriptRoot "fixtures\KioskTrustPaths.NegativeControl.cs.txt") -Raw
$decoyMatched = $false
foreach ($pattern in $patterns) {
    if ($decoy -match $pattern) {
        $decoyMatched = $true
        break
    }
}
if (-not $decoyMatched) {
    Write-Error "negative control fixture did not match trust-path write patterns"
}

$hits = New-Object System.Collections.Generic.List[string]
foreach ($dir in $scanRoots) {
    Get-ChildItem -Path $dir -Recurse -Include *.cs -File | ForEach-Object {
        if ($allowFiles -contains $_.FullName) {
            return
        }
        $lines = Get-Content -LiteralPath $_.FullName
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($pattern in $patterns) {
                if ($lines[$i] -match $pattern) {
                    $hits.Add("$($_.FullName):$($i + 1):$($lines[$i].Trim())") | Out-Null
                }
            }
        }
    }
}

if ($hits.Count -ne 0) {
    $hits | ForEach-Object { Write-Error $_ }
}

Write-Host "KioskTrustPaths static check passed ($($patterns.Count) patterns, $($hits.Count) hits)."