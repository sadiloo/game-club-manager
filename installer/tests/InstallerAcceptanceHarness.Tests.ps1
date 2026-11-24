Describe "installer acceptance harness" {
    BeforeAll {
        $script:CommonPath = (Resolve-Path (Join-Path $PSScriptRoot "..\tools\acceptance\Invoke-AcceptanceCommon.ps1")).Path
        $script:Common = Get-Content -LiteralPath $script:CommonPath -Raw
    }

    It "Start-CheckpointServerSetup does not poll HasExited (hung Inno guard)" {
        $script:Common | Should -Match "function Start-CheckpointServerSetup"
        $start = $script:Common.IndexOf("function Start-CheckpointServerSetup")
        $end = $script:Common.IndexOf("function Assert-InnoSetupLogNoFailedRuns")
        $body = $script:Common.Substring($start, $end - $start)
        $body | Should -Match "WaitForExit"
        $body | Should -Not -Match "\.HasExited"
    }
}