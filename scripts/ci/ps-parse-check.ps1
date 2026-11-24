#Requires -Version 5.1
param(
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'

$repoRoot = git rev-parse --show-toplevel 2>$null
if (-not $repoRoot) {
    $repoRoot = (Get-Location).Path
}

if (-not $Path -or $Path.Count -eq 0) {
    Push-Location $repoRoot
    try {
        $relative = @(git ls-files | Where-Object { $_ -match '\.ps1$' })
        $Path = @($relative | ForEach-Object { Join-Path $repoRoot $_ })
    } finally {
        Pop-Location
    }
} else {
    $Path = @($Path | ForEach-Object {
        if ([System.IO.Path]::IsPathRooted($_)) { $_ } else { Join-Path $repoRoot $_ }
    })
}

$hadError = $false
foreach ($file in $Path) {
    if (-not (Test-Path -LiteralPath $file)) {
        Write-Host "PS_PARSE_ERROR ${file}:0:0 file not found"
        $hadError = $true
        continue
    }

    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$parseErrors)

    if ($parseErrors -and $parseErrors.Count -gt 0) {
        foreach ($err in $parseErrors) {
            $line = $err.Extent.StartLineNumber
            $col = $err.Extent.StartColumnNumber
            $msg = $err.Message
            if (-not $msg -and $err.ErrorRecord) {
                $msg = $err.ErrorRecord.Exception.Message
            }
            if (-not $msg) { $msg = 'parse error' }
            Write-Host "PS_PARSE_ERROR ${file}:${line}:${col} $msg"
            $hadError = $true
        }
    }
}

if ($hadError) {
    exit 1
}

Write-Host "PS_PARSE_OK files=$($Path.Count)"
exit 0