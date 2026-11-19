# PR #40 R17 report

## Staging base

- `4cef504ba9fd293998890fd7083264fd78c7500a` (`cursor/pc-internet-switch-6bdd`)

## Staging head

- `61f29c54a03ebef16e74d6ebe5a7a6590c38af67` (`cursor/pr40-r17-staging`)

## Files changed

- `tests/GameClub.Kiosk.Windows.IntegrationTests/Server/L17GuardProbeSupport.cs` (new)
- `tests/GameClub.Kiosk.Windows.IntegrationTests/Server/WindowsPcInternetApplyFailureLiveTests.cs`
- `scripts/windows/ci-integration.ps1`
- `docs/reports/pr40-r17-report.md`

## Build (Linux, Release, `-p:EnableWindowsTargeting=true` where needed)

| Project | Result |
|---------|--------|
| GameClub.Kiosk.Windows.IntegrationTests net10.0 | 0 errors |
| GameClub.Kiosk.Windows.IntegrationTests net48 | 0 errors |
| GameClub.Server.TestHost | 0 errors |
| GameClub.Server.Tests | 0 errors |
| GameClub.Kiosk.Core.Tests | 0 errors |

## (a) L17 probe pipe timeout — root cause

**Evidence (R16 run 44545653691, `pr40r16_kiosk.log`):**

- Phase `dotnet-test-net48-live-checkpoint`, test `live_server_pc_internet_guard_forced_apply_failure_shows_error_and_offline_at_press`.
- Failure at ~28s: `TimeoutException` on `NamedPipeClientStream.Connect` (`GuardInternetClient.cs:29`) from `L17RealInternetOffProbe.RunAsync` line 36.
- No `L17_PROBE_PREFLIGHT` / `L17_GUARD_OFF_PROBE` lines in the log — connect failed before apply.

**Cause (harness):** The L17 pre-probe used a background `InternetPipeServer` thread with only `Task.Delay(500)` before `GuardInternetClient.ApplyAsync`. `InternetPipeServer.Run` blocks on `InternetGuardStartupFirewall.ReconcileOnPipeStart` before creating the `CheckpointGuardInternet` server instance, so the pipe often is not listening within the client’s 5s connect timeout. That is not a product pipe rejection; **Guard.exe was never started** for the probe (`GameClub.Kiosk.Guard_count=0` was not logged in R16).

**Fix (test/CI only):**

- `L17GuardProbeSupport` logs process list and `pipe_name=CheckpointGuardInternet` before the probe.
- Start real `GameClub.Kiosk.Guard` via `GuardProcessStart.StartGuard()`, wait for pipe with bounded status polling (90s, logged), run unchanged `L17RealInternetOffProbe` (real signed apply), then kill Guard before the fault-injecting harness pipe for the rest of the test.

## (b) guard-wpf watchdog (271s)

**Evidence:** `WATCHDOG TIMEOUT dotnet-test-net48-guard-wpf after 271s` at `ci-integration.ps1:648` — effective cap from `Get-EffectiveTimeoutMs` (16m global deadline minus prior phases), not the 8m phase cap.

**Fix:** Split former `dotnet-test-net48-guard-wpf` into two fresh testhost phases (`dotnet-test-net48-guard-wpf-host-timers`, `dotnet-test-net48-guard-wpf-wpf-server`), each with an explicit 5m budget. CI logs `GUARD_WPF_INVENTORY` baseline count, union count, and `diff=empty`.