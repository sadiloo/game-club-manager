# Negative-control fixture (a skipped test) for scripts/windows/Test-CheckpointPesterRunner.ps1 ONLY.
Describe "pester runner skip control" {
    It "passes" { 1 | Should -Be 1 }
    It "is skipped" -Skip { 1 | Should -Be 1 }
}
