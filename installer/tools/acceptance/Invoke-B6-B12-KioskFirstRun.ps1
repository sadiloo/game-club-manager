param(
    [string] $RepoRoot,
    [string] $AccountsFile
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Invoke-AcceptanceCommon.ps1")
Write-Host "B-6 + B-12: dotnet tests (kiosk setup, admin lock, WPF smoke)"
Push-Location $RepoRoot
try {
    & dotnet test tests/GameClub.Kiosk.Core.Tests/GameClub.Kiosk.Core.Tests.csproj -c Release -f net10.0 --filter "FullyQualifiedName~KioskSetupCoreTests|FullyQualifiedName~KioskAdminLockTests" --nologo
    if ($LASTEXITCODE -ne 0) { throw "B-6/B-12 core tests failed" }
    Invoke-WithInstallerKioskWpfLiveEnvironment -AccountsFile $AccountsFile -ScriptBlock {
        & dotnet test tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj -c Release -f net48 --filter "FullyQualifiedName~WindowsKioskWpfSmokeTests" --nologo
    }
    if ($LASTEXITCODE -ne 0) { throw "B-6 WPF tests failed" }
    Invoke-WithInstallerKioskServerVerifyTestEnvironment -ScriptBlock {
        & dotnet test tests/GameClub.Server.Tests/GameClub.Server.Tests.csproj -c Release -f net10.0 --filter "FullyQualifiedName~KioskSetupVerifyLockTests" --nologo
    }
    if ($LASTEXITCODE -ne 0) { throw "B-6 server verify tests failed" }
}
finally {
    Pop-Location
}
Write-Host "B-6 + B-12: ok"