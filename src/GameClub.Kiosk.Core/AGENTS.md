# src/GameClub.Kiosk.Core — kiosk logic (net48 and net10.0), no UI
- This library is what Linux can build and test. The test project is
  tests/GameClub.Kiosk.Core.Tests (net10.0, VSTest, xunit).
- Build: scripts/agent/build.sh src/GameClub.Kiosk.Core/GameClub.Kiosk.Core.csproj
- Tests: scripts/agent/test.sh tests/GameClub.Kiosk.Core.Tests/GameClub.Kiosk.Core.Tests.csproj --filter "FullyQualifiedName~<Area>"
- Pricing rules stay in src/GameClub.Billing. Do not add WPF or
  Windows-only APIs here.
- Never build or test src/GameClub.Kiosk (WPF, net48) or
  src/GameClub.Kiosk.Guard (net48 service) on Linux.
