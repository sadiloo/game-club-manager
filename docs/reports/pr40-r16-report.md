# PR #40 R16 report (staging `cursor/pr40-r16-staging`)

Parent: `71114ceecd86ddb94e85b479b809baa8e850d6bc`

## (a) L17 seed FK

`L17RealInternetOffProbe.SeedStationInternetDesiredOffAsync` inserted raw API station id; PostgreSQL stores normalized `stations."Id"`. **Fix (test-only):** resolve real `stations."Id"` before `station_internet_state` insert.

## (b) Host timer failures — root cause (CI / test-order, not product)

### CI runs compared

| | R13 `dba1e26` | R14 `07e524f` |
|--|--|--|
| Workflow | [44498704847](https://github.com/sadiloo/game-club-manager/actions/runs/44498704847) job `135472457387` | [44509430921](https://github.com/sadiloo/game-club-manager/actions/runs/44509430921) job `135509470398` |
| Night WPF | **FAIL** `WindowsKioskWpfSmokeTests.cs:240` Expected 1740 Actual 2160 | **PASS** after harness band fix |
| `ci-integration.ps1:601` | `throw "night WPF test failed"` — **guard-wpf never started** | Continues |
| `dotnet-test-net48-guard-wpf` | **Not in log** (no `START dotnet-test-net48-guard-wpf`) | **Starts** 11:08:13Z |
| Before guard-wpf | N/A (`guard_rules=0` would apply) | `CI_EGRESS before-dotnet-test-net48-guard-wpf guard_rules=2` (L17 probe left localhost rules from live-checkpoint) |
| Host timer in integration | Only post-failure **mutation runner** (isolated vstest per mutant) | **Same testhost** as full guard-wpf suite ~7+ minutes |
| `host_operator_stop_lock_*` | N/A in integration phase | FAIL `hubInvokeCount=2` `consecutiveFailures=1` in 50s |

`KioskHostService.cs` / `KioskHostStatusTimerTests.cs` unchanged in `dba1e26..71114ce`.

### Introducing commit and line

**`a03fc9201784f4f43777928ce57ef5ffdfb68866` (PR #40 R14), `tests/GameClub.Kiosk.Windows.IntegrationTests/Wpf/WindowsKioskWpfSmokeTests.cs` line 230** — `PcSpecialBandLiveRateCardHelper.HarnessPcNormalSpecialBandForLiveServer()` replaces `PilotSeed.PcNormalSpecialHours` so night WPF passes and CI reaches the existing bulk phase.

The guard-wpf mixer was already defined at **`scripts/windows/ci-integration.ps1` line ~608** (`guardWpfFilter` = `WindowsWpf | WindowsServer`); it did not run on R13 because the script aborted at **line 601** (`night WPF test failed`).

`07e524f` did not change `ci-integration.ps1` or host timer sources; it is the tip where the failure first appears because it includes `a03fc92`.

### Why `hubInvokeCount=2` in 40–50s (not product)

1. **Same testhost:** R14 log shows continuous `START dotnet-test-net48-guard-wpf` from 11:08:13 through host-timer failures, with HEARTBEAT diag **`TcpClientExtensions.MessageLoopAsync: Polling on remoteEndPoint: 127.0.0.1:64392`** — live SignalR to the TestHost while `KioskHostStatusTimerTests` use mock `HostTestHubTransport` (`127.0.0.1:5999`). Tests immediately before `host_operator_stop_lock_*` are other WPF timer cases in the **same** `WpfStaApplicationFixture` STA session (`wpf_remaining_time_matches_funded_session_within_one_tick` live server ~23s, then many 10–30s host scenarios).

2. **Stall mechanism:** Heartbeat status updates use `Dispatcher.Invoke` on the shared WPF STA (`KioskHostService.OnHeartbeatAcknowledged`). Long same-process runs queue dispatcher work; with only two successful hub invokes in a 50s operator-stop window, `consecutiveFailures` never reaches ≥2, so the unchanged assertion on “no server response” fails — **timer assertion semantics unchanged, timing starved by CI coupling.**

3. **R13 “green” host timers:** Integration job never entered guard-wpf; host scenarios ran only via **`scripts/agent/run-kiosk-reconnect-mutations.py`** after ci-integration abort (fresh per-target vstest).

### Fix (assertions unchanged)

- Early **`dotnet-test-net48-host-timer-fail`** phase (fresh testhost) before bulk guard-wpf; exclude those four tests from the main filter.
- Record phase exit with `FinalExitCode` pattern (no `throw`) so L17 / firewall / oracle phases still run.