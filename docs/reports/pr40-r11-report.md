# PR #40 R11 report (staging → develop gate)

## Staging ref

| Field | Value |
|-------|--------|
| **Head SHA** | `836a90c2ce98eeaae329dd31156f75d84c618cce` |
| **Parent** | `c60fae4457102d2bebc7b66068b22fa6e7d8dbf5` |
| **Branch** | `cursor/pr40-r11-staging` (pushed only here; `cursor/pc-internet-switch-6bdd` not moved) |

### Commits

- `836a90c` fix(pc-internet): PR #40 R11 — CI harness, host status tests, lease/firewall diagnostics

### Files

- `src/GameClub.Server/Kiosk/KioskServices.cs` — `LEASE_BUILD` diagnostic log (no pricing change)
- `src/GameClub.Kiosk/Services/KioskHostTestSeam.cs`, `KioskHostService.cs` — `SkipStartupRefresh`
- `tests/.../KioskHostStatusTimerTests.cs` — use seam on D5/O4D scenarios
- `tests/.../WindowsPcInternetApplyFailureLiveTests.cs` — rowVersion refresh + 409 retry
- `scripts/windows/ci-integration.ps1` — filter parentheses, vstest lists, net48 LiveCheckpoint phase, count=4
- `scripts/windows/ci-firewall-restore.ps1` — `CI_FIREWALL` / internet_off diagnostics

### Compile gate (Linux VM)

- SDK: `dotnet-install.sh --channel 10.0` → 10.0.404 (global.json 10.0.403)
- `IntegrationTests` net10.0 + net48 (`EnableWindowsTargeting=true`): **0 errors**
- `GameClub.Server.TestHost`: **0 errors**
- `GameClub.slnx`: **0 errors**
- `scripts/agent/test.sh all`: Billing 222/222, Kiosk.Core 75/75; Server.Tests failed (no Postgres on VM); integration net10 partial (expected without live server)

---

## Item 1 — reconnect / `moneyMinutes <= 0` (investigate only)

**CI symptom:** `live_server_kiosk_reconnects_when_server_unreachable_at_start` — after server returns, kiosk stays `LeaseExpired` / locked instead of `Playing` with positive display remaining.

**Evidence path:** `KioskLeaseService.BuildLeaseAsync` for funded customers calls `KioskPlayEstimate.AfterAcceptedPlay` → `CustomerPlay.Estimate` with **`session.Mode`** and **`session.PlayerCount`** (not the active segment’s mode). Wallet after settle: `settled.Account.Pc.TotalToman`. If `moneyMinutes <= 0`, `authorizedUntil` is clamped to `lastCheckpoint` → expired lease on next heartbeat.

**Exact rate-resolving line:** `src/GameClub.Billing/Accounts.cs` line **1119**:

`var quote = RateResolver.Resolve(rates, specialDays, at, groupId, mode, playerCount);`

invoked from `RemainingTimeEstimator.Estimate`, reached via `KioskPlayEstimate.AfterAcceptedPlay` (`KioskPlayEstimate.cs` ~40–48) with `mode = session.Mode`.

**Hypothesis:** Lease minutes are priced from **session header mode** (and post-settle wallet) while billing segments may be Online; after §4.3, missing Offline default lines or mismatch with segment reality can yield **0** affordable minutes even with 200k wallet at Online 80k/h. `KioskFundingGuard` still hard-codes `PlayMode.Online` at login (`KioskFundingGuard.cs` ~34) — not the lease path.

**Diagnostic added (allowed):** server stdout lines:

`LEASE_BUILD sessionId=… walletToman=… mode=… playerCount=… groupId=… hourlyRateToman=… rateSource=… band=… moneyMinutes=… authorizedUntilUtc=…`

**Proposed fix (NOT applied):** Price lease from **current segment** mode/band (same as bill); missing rate → loud failure/lock with clear error, never silent 0; test: Online session, wallet N minutes at 80k → lease authorizes N minutes.

---

## Item 4 — CI egress / firewall (investigate + CI logs only)

**CI evidence:** `CI_EGRESS internet_off public=1.1.1.1:443 blocked=False` while Guard may report apply — read-back was TCP connect only, insufficient to prove block.

**New log lines at internet_off probes** (`ci-firewall-restore.ps1`):

- `CI_FIREWALL MpsSvc status=…`
- `CI_FIREWALL active_profile name=… enabled=… in=… out=…`
- `CI_FIREWALL localhost_profile name=… enabled=… in=… out=…`
- `CI_FIREWALL ConnectionProfile name=… interface=… category=…`
- `CI_FIREWALL guard_rule_count localhost_group=Checkpoint Internet Off count=…`
- `CI_FIREWALL guard_rule name=… enabled=… dir=… action=…`
- `CI_FIREWALL guard_state_path=… present=true|false payload=…`
- (existing) `CI_EGRESS internet_off public=… blocked=…`, `CI_EGRESS internet_off checkpoint=… reachable=…`

**Options (no Guard behavior change without بن OK):**

1. Verify **real egress effect** (e.g. HTTP/TLS to 1.1.1.1, not just TCP connect) in CI evidence step.
2. Treat **localhost policy store** outbound block on every active profile as the pass criterion (align probe with `InternetFirewallApplier` verification).
3. Log Guard apply result string from harness/state when tests toggle internet.

**L17_CI missing:** Test is **net48-only**; net10 `--list-tests` counted 3 LiveCheckpoint tests → CI thought inventory ok but L17 never ran in server phase; push run failed at HTTP 409 before `Console.WriteLine` L17 line.

---

## B — `KioskHostStatusTimerTests` (×3)

**Root cause:** `KioskHostService` ctor calls `RefreshAsync()` → `EnsureRuntimeAsync()` against real `device.json` **before** tests call `ResetForReconfigureAsync` / `ConfigureForTests`. Race: production connect/lock UI (`Locked — … · connected`) and `KioskHubLifecycle.WaitForTransportConnectedAsync` timeout at line 81.

**Fix:** `KioskHostTestSeam.SkipStartupRefresh`; D5 and O4D scenarios pass it. Tests existed on `main` @5574ce0; failures are timing/race on windows-2022, not missing tests.

---

## C — vstest filter / night test rerun

**Root cause:** Filter `Category=WindowsWpf|Category=WindowsServer&FullyQualifiedName!~wpf_remaining_time…` — `&` tighter than `|`, so night test ran again in day/guard phase.

**Fix:** `(Category=WindowsWpf)|(Category=WindowsServer&Category!=LiveCheckpointServer&FullyQualifiedName!~wpf_remaining_time…)`.

**Proof:** `VSTEST_LIST phase=… framework=… count=…` + per-test `VSTEST_LIST_TEST` lines before each phase.

---

## D — L17 apply-failure live test

**409 root cause:** `expectedVersion` read from grid **before** pairing/login/runtime start; grid `rowVersion` advanced during setup → stale version on internet POST.

**Fix:** `ReadStationRowVersionAsync` immediately before toggle; retry once on 409 with fresh version.

**Missing run:** L17 is net48-only; guard filter + no dedicated phase; LIVE count used net10 list only (3 ≠ 4).

**Fix:** Union net10+net48 LiveCheckpoint inventory (`LIVE_CHECKPOINT_TEST_COUNT` must be **4**); dedicated `dotnet-test-net48-live-checkpoint` phase; exclude `LiveCheckpointServer` from guard-wpf batch to avoid double-run.

**L17_CI line format (unchanged):** `L17_CI internetError=… offlineAtPressUtc=… pressAtUtc=…`

---

## Await develop

Staging pushed. **Do not** fast-forward `cursor/pc-internet-switch-6bdd` until develop posts **`GATE OK 836a90c…`** (or exact staging head).