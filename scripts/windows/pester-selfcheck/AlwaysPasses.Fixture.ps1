# Positive-control fixture for scripts/windows/Test-CheckpointPesterRunner.ps1 ONLY. Not part of any real suite.
Describe "pester runner positive control" {
    It "passes" { 1 | Should -Be 1 }
}
