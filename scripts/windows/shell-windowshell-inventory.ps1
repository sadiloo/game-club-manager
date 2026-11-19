param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Excluded", "Shell")]
    [string] $Role
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Set-Location $repoRoot

$testProj = "tests/GameClub.Shell.Windows.IntegrationTests/GameClub.Shell.Windows.IntegrationTests.csproj"
$categoryFilter = "Category=WindowsShell"
$slnxPath = "GameClub.slnx"
$slnxShellFilter = "FullyQualifiedName~GameClub.Shell.Windows.IntegrationTests&Category=WindowsShell"

function Invoke-ListTests {
    param(
        [string] $ProjectOrSln,
        [string] $Filter
    )
    $args = @(
        "test", $ProjectOrSln,
        "-c", "Release",
        "-f", "net48",
        "--nologo",
        "--list-tests",
        "--filter", $Filter
    )
    $raw = & dotnet @args 2>&1 | ForEach-Object { $_.ToString() }
    $started = $false
    $names = [System.Collections.Generic.List[string]]::new()
    foreach ($line in $raw) {
        if ($line -match '^The following Tests are available:') {
            $started = $true
            continue
        }
        if (-not $started) { continue }
        $trim = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trim)) { continue }
        if ($trim.StartsWith("Test run for")) { break }
        if ($trim.Contains("FileNotFoundException") -or $trim.StartsWith("System.")) { break }
        $names.Add($trim)
    }
    return $names
}

dotnet build $testProj -c Release -f net48 --nologo | Out-Null

$categoryNames = Invoke-ListTests -ProjectOrSln $testProj -Filter $categoryFilter
$slnxNames = Invoke-ListTests -ProjectOrSln $slnxPath -Filter $slnxShellFilter

if ($categoryNames.Count -ne $slnxNames.Count) {
    Write-Error "Shell WindowsShell inventory mismatch: project Category count=$($categoryNames.Count) slnx count=$($slnxNames.Count)"
}

$categorySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($n in $categoryNames) { [void]$categorySet.Add($n) }
foreach ($n in $slnxNames) {
    if (-not $categorySet.Contains($n)) {
        Write-Error "Shell WindowsShell inventory mismatch: slnx listed test not in Category list: $n"
    }
}

switch ($Role) {
    "Excluded" {
        foreach ($name in ($categoryNames | Sort-Object)) {
            Write-Output "SHELL_EXCLUDED_TEST $name"
        }
        Write-Output "SHELL_EXCLUDED_COUNT=$($categoryNames.Count)"
    }
    "Shell" {
        foreach ($name in ($categoryNames | Sort-Object)) {
            Write-Output "SHELL_LIST_TEST $name"
        }
        Write-Output "SHELL_LIST_COUNT=$($categoryNames.Count)"
    }
}