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
        $body | Should -Match "Get-Process CheckpointServerSetup"
        $body | Should -Match "WaitForExit leader"
    }
}