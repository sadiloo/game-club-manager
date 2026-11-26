$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$toolRoots = @(
    (Join-Path $root "installer/tools"),
    (Join-Path $root "installer/ci")
)
$patterns = @(
    @{ Name = "utf8NoBOM encoding"; Regex = '-Encoding\s+utf8NoBOM' },
    @{ Name = "null-coalescing ??"; Regex = '\?\?' },
    @{ Name = "null-conditional ?."; Regex = '\?\.' },
    @{ Name = "ForEach-Object -Parallel"; Regex = 'ForEach-Object\s+-Parallel' }
)
# PS7-only syntax found through the AST (works whether this runs under PowerShell 7 or powershell.exe 5.1:
# under 5.1 such syntax is a parse error, which is reported too).
$ps7AstTypes = @{
    "TernaryExpressionAst" = "ternary ?:"
    "PipelineChainAst"     = "pipeline chain && / ||"
}
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
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    if ($errors -and $errors.Count -gt 0) {
        throw "PS5.1 incompatibility (parse error: $($errors[0].Message)) in $($file.FullName):$($errors[0].Extent.StartLineNumber)"
    }
    $hit = $ast.Find({ param($n) $ps7AstTypes.ContainsKey($n.GetType().Name) }, $true)
    if ($hit) {
        throw "PS5.1 incompatibility ($($ps7AstTypes[$hit.GetType().Name])) in $($file.FullName):$($hit.Extent.StartLineNumber)"
    }
}
Write-Host "PowerShell51InstallerStatic: ok ($($files.Count) files)"
