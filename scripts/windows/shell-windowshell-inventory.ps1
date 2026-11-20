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

function Invoke-ListTests {
    param(
        [string] $Project,
        [string] $Filter
    )
    $args = @(
        "test", $Project,
        "-c", "Release",
        "-f", "net48",
        "--nologo",
        "--list-tests"
    )
    if (-not [string]::IsNullOrWhiteSpace($Filter)) {
        $args += @("--filter", $Filter)
    }
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

$projectNames = Invoke-ListTests -Project $testProj -Filter $null

switch ($Role) {
    "Excluded" {
        foreach ($name in ($projectNames | Sort-Object)) {
            Write-Output "SHELL_EXCLUDED_TEST $name"
        }
        Write-Output "SHELL_EXCLUDED_COUNT=$($projectNames.Count)"
    }
    "Shell" {
        $categoryNames = Invoke-ListTests -Project $testProj -Filter $categoryFilter
        if ($projectNames.Count -ne $categoryNames.Count) {
            Write-Error "Shell WindowsShell inventory mismatch: project count=$($projectNames.Count) Category=WindowsShell count=$($categoryNames.Count)"
        }
        $categorySet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($n in $categoryNames) { [void]$categorySet.Add($n) }
        foreach ($n in $projectNames) {
            if (-not $categorySet.Contains($n)) {
                Write-Error "Shell WindowsShell inventory mismatch: project test missing Category=WindowsShell: $n"
            }
        }
        Write-Output "SHELL_PROJECT_COUNT=$($projectNames.Count)"
        foreach ($name in ($categoryNames | Sort-Object)) {
            Write-Output "SHELL_LIST_TEST $name"
        }
        Write-Output "SHELL_LIST_COUNT=$($categoryNames.Count)"
    }
}