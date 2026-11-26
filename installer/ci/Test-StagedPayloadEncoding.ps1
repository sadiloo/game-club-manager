# DEF-19: every SHIPPED .ps1/.psm1 in a staged payload must start with a UTF-8 BOM or be pure ASCII, and must parse
# the way Windows PowerShell 5.1 reads it. PS 5.1 decodes a file without a BOM in the system ANSI code page, so a
# BOM-less UTF-8 file with Persian text (e.g. U+0654 = D9 94; cp1252/cp1256 0x94 = U+201D, a quote) stops parsing.
#   Under Windows PowerShell 5.1 (CI: powershell.exe) the real loader parses each file: (Get-Command <path>).ScriptBlock,
#   plus the explicit decode with [Text.Encoding]::Default (the ANSI code page of the runner).
#   Under PowerShell 7 (box) the 5.1 read is emulated: no BOM => decode as -AnsiCodePage (default 1252 and 1256).
param(
    [Parameter(Mandatory)][string[]] $Dirs,
    [int[]] $AnsiCodePage = @(1252, 1256)
)
$ErrorActionPreference = "Stop"
$isWinPs = $PSVersionTable.PSEdition -ne "Core"
if (-not $isWinPs) { [System.Text.Encoding]::RegisterProvider([System.Text.CodePagesEncodingProvider]::Instance) }

function Test-Utf8Bom([byte[]] $b) { return ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF) }
function Test-PureAscii([byte[]] $b) { foreach ($x in $b) { if ($x -gt 0x7F) { return $false } }; return $true }
function Get-ParseErrors([string] $text, [string] $path) {
    $tokens = $null; $errs = $null
    [void][System.Management.Automation.Language.Parser]::ParseInput($text, $path, [ref]$tokens, [ref]$errs)
    return @($errs)
}

$problems = New-Object System.Collections.Generic.List[string]
$count = 0
foreach ($dir in $Dirs) {
    if (-not (Test-Path -LiteralPath $dir)) { throw "Test-StagedPayloadEncoding: staged dir missing: $dir" }
    $files = @(Get-ChildItem -LiteralPath $dir -File | Where-Object { $_.Extension -in ".ps1", ".psm1" })
    if ($files.Count -eq 0) { throw "Test-StagedPayloadEncoding: no .ps1/.psm1 in $dir" }
    foreach ($f in $files) {
        $count++
        $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
        $bom = Test-Utf8Bom $bytes
        $ascii = Test-PureAscii $bytes
        if (-not $bom -and -not $ascii) {
            $problems.Add("$($f.FullName): non-ASCII bytes and no UTF-8 BOM (Windows PowerShell 5.1 reads it as ANSI)")
        }
        $decodings = @()
        if ($bom) {
            $decodings += , @("utf8-bom", (New-Object System.Text.UTF8Encoding $true).GetString($bytes, 3, $bytes.Length - 3))
        } elseif ($isWinPs) {
            $decodings += , @("ansi-default-cp$([System.Text.Encoding]::Default.CodePage)", [System.Text.Encoding]::Default.GetString($bytes))
        } else {
            foreach ($cp in $AnsiCodePage) { $decodings += , @("ansi-cp$cp", [System.Text.Encoding]::GetEncoding($cp).GetString($bytes)) }
        }
        foreach ($d in $decodings) {
            $errs = Get-ParseErrors $d[1] $f.FullName
            if ($errs.Count -gt 0) {
                $problems.Add("$($f.FullName) [$($d[0])]: $($errs.Count) parse error(s); first: line $($errs[0].Extent.StartLineNumber): $($errs[0].Message)")
            }
        }
        if ($isWinPs -and $f.Extension -eq ".ps1") {
            # .psm1 has no ExternalScriptInfo; it is covered by the ANSI-default decode above.
            try { $null = (Get-Command -Name $f.FullName -ErrorAction Stop).ScriptBlock }
            catch { $problems.Add("$($f.FullName) [powershell.exe loader]: $($_.Exception.Message)") }
        }
        Write-Host ("Test-StagedPayloadEncoding: {0} bom={1} ascii={2} {3}" -f $f.Name, $bom, $ascii, $(if ($isWinPs) { "ps=$($PSVersionTable.PSVersion)" } else { "emulated=" + ($AnsiCodePage -join ",") }))
    }
}
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "Test-StagedPayloadEncoding: FAIL $_" }
    throw "Test-StagedPayloadEncoding: $($problems.Count) problem(s) in the staged payload"
}
Write-Host "Test-StagedPayloadEncoding: ok files=$count edition=$($PSVersionTable.PSEdition) version=$($PSVersionTable.PSVersion)"
