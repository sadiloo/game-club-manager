Describe "installer acceptance harness" {
    BeforeAll {
        $script:CommonPath = (Resolve-Path (Join-Path $PSScriptRoot "..\tools\acceptance\Invoke-AcceptanceCommon.ps1")).Path
        $script:Common = Get-Content -LiteralPath $script:CommonPath -Raw
    }

    It "Start-CheckpointServerSetup traces blocking harness calls" {
        $script:Common | Should -Match "function Start-CheckpointServerSetup"
        $script:Common | Should -Match "function Write-AcceptanceHarnessTrace"
        $start = $script:Common.IndexOf("function Start-CheckpointServerSetup")
        $end = $script:Common.IndexOf("function Assert-InnoSetupLogNoFailedRuns")
        $body = $script:Common.Substring($start, $end - $start)
        $body | Should -Match "Write-AcceptanceHarnessTrace"
        # Stale since 4a6ae3b (file was never run in CI): the process scan moved into a helper; pin both.
        $body | Should -Match "Get-CheckpointServerSetupProcesses"
        $script:Common | Should -Match ([regex]::Escape('$_.ProcessName -like "CheckpointServerSetup*"'))
        $body | Should -Match "WaitForExit leader"
        $body | Should -Match "Assert-NoCheckpointSetupProcesses"
        $body | Should -Match "Get-SetupLegDescendantPids"
        $script:Common | Should -Match "function Install-ServerSetup"
    }

    It "Install-ServerSetup does not pass scriptblock empty $args to Inno" {
        $script:Common | Should -Match '\$setupArgs'
        $script:Common | Should -Not -Match 'ArgumentList \$args'
    }

    It "ExpectExitCode 0 legs (seed installs) must fail on a non-zero setup exit (R10e)" {
        $start = $script:Common.IndexOf("function Start-CheckpointServerSetup")
        $end = $script:Common.IndexOf("function Assert-InnoSetupLogNoFailedRuns")
        $body = $script:Common.Substring($start, $end - $start)
        $body | Should -Match ([regex]::Escape('if ($exitCode -ne 0 -and -not $CallerChecksExit) {'))
        $body | Should -Match ([regex]::Escape('throw "setup leg pin: exit $exitCode expected 0 (leg=$LegLabel)"'))
        ([regex]::Matches($script:Common, '-CallerChecksExit\b')).Count | Should -Be 1
    }

    It "Invoke-AcceptanceDotnetTest rejects a filter that matched no tests and a failed run (R10e leg audit)" {
        $tokens = $null; $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:CommonPath, [ref]$tokens, [ref]$parseErrors)
        $fn = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq "Invoke-AcceptanceDotnetTest" }, $true))
        $fn.Count | Should -Be 1
        . ([scriptblock]::Create($fn[0].Extent.Text))
        function dotnet {
            foreach ($l in $script:FakeDotnetLines) { $l }
            $global:LASTEXITCODE = $script:FakeDotnetExit
        }
        $script:FakeDotnetExit = 0
        $script:FakeDotnetLines = @("No test matches the given testcase filter ``FullyQualifiedName~Nope``")
        { Invoke-AcceptanceDotnetTest -Label "t-empty" -DotnetArgs @("test") 6>$null } | Should -Throw "*filter matched no tests*"
        $script:FakeDotnetLines = @("Passed!  - Failed:     0, Passed:     0, Skipped:     0, Total:     0")
        { Invoke-AcceptanceDotnetTest -Label "t-zero" -DotnetArgs @("test") 6>$null } | Should -Throw "*filter matched no tests*"
        $script:FakeDotnetExit = 1
        $script:FakeDotnetLines = @("Failed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3")
        { Invoke-AcceptanceDotnetTest -Label "t-fail" -DotnetArgs @("test") 6>$null } | Should -Throw "*dotnet test failed exit 1*"
        $script:FakeDotnetExit = 0
        $script:FakeDotnetLines = @("Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 1 s")
        { Invoke-AcceptanceDotnetTest -Label "t-ok" -DotnetArgs @("test") 6>$null } | Should -Not -Throw
        $b6 = Get-Content -LiteralPath (Join-Path (Split-Path $script:CommonPath) "Invoke-B6-B12-KioskFirstRun.ps1") -Raw
        ([regex]::Matches($b6, 'Invoke-AcceptanceDotnetTest\s+-Label')).Count | Should -Be 3
        $b6 | Should -Not -Match '(?m)^\s*dotnet\s+test\b'
    }

    It "Start-CheckpointServerSetup must not rewrite setup exit codes (DEF-8)" {
        $script:Common | Should -Not -Match "treating as exit"
    }
}