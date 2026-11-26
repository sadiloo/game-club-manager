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
        $body | Should -Match ([regex]::Escape('if (-not $CallerChecksExit) {'))
        $body | Should -Match ([regex]::Escape('throw "setup leg pin: exit $exitCode expected 0 (leg=$LegLabel) $abort"'))
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

    It "DEF-21: a non-zero Setup exit is reported with its real code and Inno phase, never as 'Log closed' missing" {
        $start = $script:Common.IndexOf("function Start-CheckpointServerSetup")
        $end = $script:Common.IndexOf("function Assert-InnoSetupLogNoFailedRuns")
        $body = $script:Common.Substring($start, $end - $start)
        $nonZero = $body.IndexOf('            if ($exitCode -ne 0) {')
        $logClosed = $body.IndexOf('Assert-InnoLogClosedForLeg -LogPath')
        $nonZero | Should -BeGreaterThan 0
        $logClosed | Should -BeGreaterThan $nonZero
        $branch = $body.Substring($nonZero, $logClosed - $nonZero)
        $branch | Should -Match ([regex]::Escape('return [PSCustomObject]@{ Process = $proc; ExitCode = $exitCode; LegLabel = $LegLabel; Abort = $abort }'))
        $script:Common | Should -Match ([regex]::Escape('throw "setup exit $($result.ExitCode) leg=$LegLabel exe=$Exe $(Get-InnoLogPostinstallAbort -LogPath $logPath)"'))

        $tokens = $null; $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script:CommonPath, [ref]$tokens, [ref]$parseErrors)
        $fn = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq "Get-InnoLogPostinstallAbort" }, $true))
        $fn.Count | Should -Be 1
        . ([scriptblock]::Create($fn[0].Extent.Text))
        $log = Join-Path ([IO.Path]::GetTempPath()) ("cp-def21-" + [guid]::NewGuid().ToString("N") + ".log")
        try {
            # Shape of the CI log on d71a6a0 (run 45834358940): ParserError, then ABORT; no "Log closed.".
            @(
                "2026-11-26 14:05:42.041   CHECKPOINT_POSTINSTALL_EXEC phase=database",
                "2026-11-26 14:05:42.229   The string is missing the terminator: `".",
                "2026-11-26 14:05:42.291   CHECKPOINT_POSTINSTALL_ABORT phase=database exit=135",
                "2026-11-26 14:05:42.291   CHECKPOINT_POSTINSTALL_FAILED CurRunRecId=0 phase=database exit=135"
            ) | Set-Content -LiteralPath $log -Encoding UTF8
            Get-InnoLogPostinstallAbort -LogPath $log | Should -Be "CHECKPOINT_POSTINSTALL_ABORT phase=database exit=135"
            "x" | Set-Content -LiteralPath $log -Encoding UTF8
            Get-InnoLogPostinstallAbort -LogPath $log | Should -Be ""
        } finally { Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue }
    }

    It "Start-CheckpointServerSetup must not rewrite setup exit codes (DEF-8)" {
        $script:Common | Should -Not -Match "treating as exit"
    }
}