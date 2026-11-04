# src/GameClub.Kiosk.Core — kiosk logic (net48 and net10.0), no UI
- This library is what Linux runs tests for. The test project is
  tests/GameClub.Kiosk.Core.Tests (net10.0, VSTest, xunit).
- Build: scripts/agent/build.sh src/GameClub.Kiosk.Core/GameClub.Kiosk.Core.csproj
- Tests: scripts/agent/test.sh tests/GameClub.Kiosk.Core.Tests/GameClub.Kiosk.Core.Tests.csproj --filter "FullyQualifiedName~<Area>"
  On Linux the script adds -f net10.0 unless you pass -f or --framework.
- Pricing rules stay in src/GameClub.Billing. Do not add WPF or
  Windows-only APIs here.
- Do not run or test src/GameClub.Kiosk or src/GameClub.Kiosk.Guard on
  Linux. Compile-only builds of those two are allowed.
