# Negative-control fixture for scripts/windows/Test-CheckpointPesterRunner.ps1 ONLY. Not part of any real suite.
Describe "pester runner negative control" {
    It "passes" { 1 | Should -Be 1 }
    It "deliberately fails" { 1 | Should -Be 2 }
}
