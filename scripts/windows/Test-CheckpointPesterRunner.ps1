# Self-check for Invoke-CheckpointPesterFile.ps1 (R10e item 2 negative control). Each fixture runs in a CHILD
# process of the current PowerShell host; a failing/empty/skipped fixture must give a non-zero exit, a passing one 0.
$ErrorActionPreference = "Stop"
$runner = Join-Path $PSScriptRoot "Invoke-CheckpointPesterFile.ps1"
$fixtures = Join-Path $PSScriptRoot "pester-selfcheck"
$hostExe = (Get-Process -Id $PID).Path
if ([System.IO.Path]::GetFileNameWithoutExtension([string]$hostExe) -eq "dotnet") {
    # PowerShell installed as a dotnet global tool: the host process is dotnet; use the pwsh shim instead.
    $hostExe = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
}
if ([string]::IsNullOrWhiteSpace($hostExe) -or -not (Test-Path -LiteralPath $hostExe)) { throw "pester self-check: host exe not found" }

$cases = @(
    @{ Fixture = "AlwaysFails.Fixture.ps1"; ExpectZero = $false },
    @{ Fixture = "NoTests.Fixture.ps1"; ExpectZero = $false },
    @{ Fixture = "Skipped.Fixture.ps1"; ExpectZero = $false },
    @{ Fixture = "AlwaysPasses.Fixture.ps1"; ExpectZero = $true }
)
foreach ($c in $cases) {
    $fixture = Join-Path $fixtures $c.Fixture
    $out = & $hostExe -NoProfile -NonInteractive -File $runner -Path $fixture 2>&1 | Out-String
    $code = $LASTEXITCODE
    Write-Host "pester self-check: fixture=$($c.Fixture) exit=$code"
    if ($c.ExpectZero -and $code -ne 0) { throw "pester self-check: $($c.Fixture) must exit 0; got $code`n$out" }
    if (-not $c.ExpectZero -and $code -eq 0) { throw "pester self-check: $($c.Fixture) must exit non-zero; got 0`n$out" }
    if (-not $c.ExpectZero -and $out -notmatch 'Invoke-CheckpointPesterFile: FAILED \(result=') {
        throw "pester self-check: $($c.Fixture) failed for the wrong reason (runner verdict line missing)`n$out"
    }
    if ($c.ExpectZero -and $out -notmatch 'Invoke-CheckpointPesterFile: result=Passed total=1 passed=1 failed=0') {
        throw "pester self-check: $($c.Fixture) passed without the runner summary`n$out"
    }
}

# Part 2: the REAL dispatch loop of ci-installer.ps1 (extracted by AST, not re-typed) must fail when a Describe file
# fails. It runs in a child process against a temp root, so the fixtures never become part of installer/tests.
$ciInstaller = Join-Path $PSScriptRoot "ci-installer.ps1"
$tokens = $null; $parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($ciInstaller, [ref]$tokens, [ref]$parseErrors)
$loops = @($ast.FindAll({ param($n)
    $n -is [System.Management.Automation.Language.ForEachStatementAst] -and
    $n.Variable.VariablePath.UserPath -eq "name" -and $n.Condition.Extent.Text -eq '$staticTests' }, $true))
if ($loops.Count -ne 1) { throw "pester self-check: expected exactly one 'foreach (`$name in `$staticTests)' loop in ci-installer.ps1; found $($loops.Count)" }
$loopText = $loops[0].Extent.Text
foreach ($c in @(
        @{ Fixture = "AlwaysFails.Fixture.ps1"; ExpectZero = $false },
        @{ Fixture = "AlwaysPasses.Fixture.ps1"; ExpectZero = $true })) {
    $tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ckpt-pester-loop-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path (Join-Path $tmpRoot "installer/tests"), (Join-Path $tmpRoot "scripts/windows") | Out-Null
    try {
        Copy-Item -LiteralPath $runner -Destination (Join-Path $tmpRoot "scripts/windows/Invoke-CheckpointPesterFile.ps1")
        Copy-Item -LiteralPath (Join-Path $fixtures $c.Fixture) -Destination (Join-Path $tmpRoot "installer/tests/SelfCheck.Tests.ps1")
        $driver = Join-Path $tmpRoot "loop-driver.ps1"
        Set-Content -LiteralPath $driver -Encoding UTF8 -Value @(
            '$ErrorActionPreference = "Stop"',
            ('$root = ''' + $tmpRoot.Replace("'", "''") + ''''),
            '$staticTests = @("SelfCheck.Tests.ps1")',
            $loopText)
        $out = & $hostExe -NoProfile -NonInteractive -File $driver 2>&1 | Out-String
        $code = $LASTEXITCODE
        Write-Host "pester self-check: ci-installer loop fixture=$($c.Fixture) exit=$code"
        if ($c.ExpectZero -and ($code -ne 0 -or $out -notmatch 'Invoke-CheckpointPesterFile: result=Passed total=1 passed=1 failed=0')) {
            throw "pester self-check: ci-installer loop must pass $($c.Fixture) through the runner; exit=$code`n$out"
        }
        if (-not $c.ExpectZero -and ($code -eq 0 -or $out -notmatch 'Invoke-CheckpointPesterFile: FAILED \(result=')) {
            throw "pester self-check: ci-installer loop must FAIL on $($c.Fixture) via the runner verdict; exit=$code`n$out"
        }
    } finally {
        Remove-Item -LiteralPath $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Host "pester self-check: ok"
