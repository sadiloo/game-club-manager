param(
    [string] $RepoRoot,
    [string] $AccountsFile
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
Write-Host "B-6 + B-12: dotnet tests (kiosk setup, admin lock, WPF smoke)"
Push-Location $RepoRoot
try {
    Invoke-AcceptanceDotnetTest -Label "B-6/B-12 core tests" -DotnetArgs @(
        "test", "tests/GameClub.Kiosk.Core.Tests/GameClub.Kiosk.Core.Tests.csproj", "-c", "Release", "-f", "net10.0",
        "--filter", "FullyQualifiedName~KioskSetupCoreTests|FullyQualifiedName~KioskAdminLockTests", "--nologo")
    Invoke-WithInstallerKioskWpfLiveEnvironment -AccountsFile $AccountsFile -ScriptBlock {
        Invoke-AcceptanceDotnetTest -Label "B-6 WPF tests" -DotnetArgs @(
            "test", "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj", "-c", "Release", "-f", "net48",
            "--filter", "FullyQualifiedName~WindowsKioskWpfSmokeTests&FullyQualifiedName!~wpf_remaining_time_matches_funded_session_special_band_night_line", "--nologo")
    }
    Invoke-WithInstallerKioskServerVerifyTestEnvironment -ScriptBlock {
        Invoke-AcceptanceDotnetTest -Label "B-6 server verify tests" -DotnetArgs @(
            "test", "tests/GameClub.Server.Tests/GameClub.Server.Tests.csproj", "-c", "Release", "-f", "net10.0",
            "--filter", "FullyQualifiedName~KioskSetupVerifyLockTests", "--nologo")
    }
    Ensure-InstallerLiveOperatorUsersFromServerJson
}
finally {
    Pop-Location
}
Write-Host "B-6 + B-12: ok"