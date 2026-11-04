# src/GameClub.Billing — netstandard2.0 and net10.0 (used by the net48 kiosk and src/GameClub.Server)
- Build: scripts/agent/build.sh src/GameClub.Billing/GameClub.Billing.csproj
- Tests: scripts/agent/test.sh tests/GameClub.Billing.Tests/GameClub.Billing.Tests.csproj --filter "FullyQualifiedName~<Area>"
  On Linux the script adds -f net10.0 unless you pass -f or --framework.
- Money is long toman, integer math only (MoneyMath.CheckedAdd/Multiply); never decimal/double/float.
- Time: DateTimeOffset normalised with TehranTime.Ensure/FromUtc (+03:30); never DateTime.Now; dates are LocalDate (Jalali via JalaliCalendar).
- Must compile for netstandard2.0: use Bcl.TryAdd, no DateOnly/TimeOnly/IReadOnlySet in public API.
- Every pricing rule in the spec gets a test with the spec's example numbers.
- Always cover: zero/one minute, crossing midnight, rate change mid-session,
  rounding at the boundary.
- Never skip, delete or loosen billing tests.
