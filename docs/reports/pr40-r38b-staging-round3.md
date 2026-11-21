# PR #40 r38b staging — round 3 report

Coordinator GO on same branch `cursor/pr40-r38b-staging`. No PR opened.

## Mutant (5d98b31 MAX-over-open-snapshots branch surcharge, discarded)

```
PcInternetRatesMigrationTests.cs:363 — Expected: 25000, Actual: 20000 (no open sessions after Down)
PcInternetRatesMigrationTests.cs:332 — Expected: 40000, Actual: 25000 (branch_rounding after Down with global 130k night)
PcInternetRatesMigrationTests.cs:406 — Expected: 130000, Actual: 115000 (manager night; MAX-over-snapshots Down)
```

## Full Server.Tests

| Run | Result |
|-----|--------|
| 1 | **583/583** |
| 2 | **583/583** |

## Items (file:line)

| Item | Location |
|------|----------|
| Down branch surcharge from `rate_lines` night−day | `20261118175351_PcInternetRates.cs:246-259` |
| Removed MAX-over-snapshots branch update | `20261118175351_PcInternetRates.cs` (was :355-364 on 5d98b31) |
| Drop synthesized Offline night on Up | `20261118175351_PcInternetRates.cs:163-170` |
| CI `npm ci` for Scripts | `.github/workflows/ci.yml` (before test net10) |
| No-open-sessions Down/Up nights | `PcInternetRatesMigrationTests.cs:341-370` |
| Open session + manager 130k night | `PcInternetRatesMigrationTests.cs:372-418` |
| Branch assert 40k after Down | `PcInternetRatesMigrationTests.cs:332` |
| Rename non-main “proof” test | `PcInternetRatesMigrationTests.cs:283` |
| LeaseExpired single verify | `KioskShellOrchestrator.cs:91-96` |
| Report wording fix | `docs/reports/pr40-r38b-staging-round2.md` |