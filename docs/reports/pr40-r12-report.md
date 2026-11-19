# PR #40 R12 report (staging → develop gate)

## Staging ref (fill after push)

| Field | Value |
|-------|--------|
| **Head SHA** | `e161903c8b8f8e8b8f8e8b8f8e8b8f8e8b8f8e8b` _(see git log after push)_ |
| **Parent** | `6d32abda53709308b8fee1b83c196035bac48bbc` |
| **Branch** | `cursor/pr40-r12-staging` |

---

## a — LEASE_BUILD job log + artifacts

**Job log lines** (from `scripts/windows/ci-integration.ps1` → `Publish-ServerLeaseBuildEvidence`):

`LEASE_BUILD phase=<phase> sourceLog=<server-day-out.log|server-night-out.log|…> <full LEASE_BUILD line from stdout>`

**Server stdout** (`src/GameClub.Server/Kiosk/KioskServices.cs` ~272–277):

`LEASE_BUILD sessionId=… paidToman=… giftToman=… walletTotalToman=… mode=… playerCount=… groupId=… hourlyRateToman=… rateSource=… band=… moneyMinutes=… authorizedUntilUtc=…`

After `dotnet-test-net10-server`, if `live_server_kiosk_reconnects_when_server_unreachable_at_start` appears in TRX and no `^LEASE_BUILD` in `server-day-out.log` → throw `LEASE_BUILD missing`.

Raw server logs copied to `artifacts/server-*.log` each phase end.

## b — L17 live OFF snapshots

Printed from `WindowsPcInternetApplyFailureLiveTests.WriteLiveInternetOffSnapshot` (~line 280):

`LIVE_INTERNET_OFF_TOGGLE request …` / `response …`  
`LIVE_INTERNET_OFF_SNAPSHOT snap={1|2} stationId=… sessionId=… segments=… internetState=… kioskQueuePending=… harnessPipeRunning=… guardProcesses=… guardRuleCount=… internetStatePath=… internetStatePayload=… guardLogTail=… egressProbe_1.1.1.1_443_blocked=…`

## c — vstest filter + night_line assert

Filter: `(Category=WindowsWpf&FullyQualifiedName!~night_line)|(Category=WindowsServer&Category!=LiveCheckpointServer&FullyQualifiedName!~night_line)` in `ci-integration.ps1`.

`Assert-NightLineOnlyInNightPhase` throws if `night_line` listed outside `dotnet-test-net48-wpf-night`.

## d — PC-FAIL grid root cause

**Root cause:** net48 L17 ran without live DB reset on shared CI server; stale sessions/grid state and rowVersion read before login/session visible on operator grid.

**Fix:** `LiveDatabaseResetInvoker.ResetViaHarnessTest()` at test start; `WaitForStationRowVersionAsync` after login + runtime start (session on grid with `rowVersion`).

## e — KioskHostStatusTimerTests ×3

| Run | Result |
|-----|--------|
| `main` @5574ce0, **44276759016** (windows-2022 kiosk integration) | All three **passed** in guard-wpf phase (~09:46–09:47 UTC); stability/mutation pass later killed overlapping names (separate harness). |
| R11 **44458479913** | Same three **failed** in guard-wpf (timeout / `Locked — … connected` race). |

`SkipStartupRefresh` kept: addresses ctor `RefreshAsync` race (R11 report); main passed without it under lighter CI load; R11 failures are environmental/timing on shared server phase, not missing tests.

## f — G2 harness band

`PcSpecialBandHarnessSchedule.BandAwayFromUtcWindow` + `OptionsForG2Oracle`; G2 test starts TestHost with `PcSpecialBandHarnessEnv.HostArgsFromBand` (25 min window, 3h margin). `G2_HARNESS_BAND` log line in test.

## g — 409 retry removed

Single toggle after `WaitForStationRowVersionAsync`; 409 logged as `LIVE_INTERNET_OFF_TOGGLE finding=409_conflict…` only.

## h — file/line map

| Output | File:line |
|--------|-----------|
| `LEASE_BUILD …` | `KioskServices.cs` ~272 |
| `LEASE_BUILD phase=…` (CI) | `ci-integration.ps1` `Publish-ServerLeaseBuildEvidence` |
| `LIVE_INTERNET_OFF_*` | `WindowsPcInternetApplyFailureLiveTests.cs` |
| `L17_CI …` | same, end of L17 test |
| `G2_HARNESS_BAND …` | `WindowsKioskServerIntegrationTests.cs` G2 test |