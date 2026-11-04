# src/GameClub.Billing — netstandard2.0 and net10.0 (used by the net48 kiosk and the future server)
- Build: scripts/agent/build.sh src/GameClub.Billing/GameClub.Billing.csproj
- Tests: scripts/agent/test.sh tests/GameClub.Billing.Tests/GameClub.Billing.Tests.csproj --filter "FullyQualifiedName~<Area>"
- Money is decimal, never double/float. Store time as UTC (DateTimeOffset).
- Stay compatible with netstandard2.0: no APIs newer than netstandard2.0.
- Every pricing rule in the spec gets a test with the spec's example numbers.
- Always cover: zero/one minute, crossing midnight, rate change mid-session,
  rounding at the boundary.
- Never skip, delete or loosen billing tests.
