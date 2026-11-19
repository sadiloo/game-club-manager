# PR #40 R18c report

## Staging

| Field | Value |
|-------|--------|
| **Branch** | `cursor/pr40-r18c-staging` (fast-forward only) |
| **Parent** | `717a3c1ae499d648464530975db3799f6f85722a` |
| **Staging sha** | `a219008` |

## Item status

| # | Status |
|---|--------|
| 1 | Client error mapping — **done** (prior commit) |
| 2 | 180 s hang — **root-caused (product STOP, not applied)** |
| 3 | Merged apply budgets — **done** |
| 4 | Orphan harness — **done** (prior commit) |
| 5 | `within_one_tick` — **analysed; product STOP candidates (not applied)** |

---

## (3) Budget table (measured → 3× → chosen)

| Step | Measured max on runner (ms) | 3× (ms) | Chosen budget (ms) |
|------|------------------------------|---------|---------------------|
| reconcile `localhost_cleanup` | 4178 | 12 534 | **13 000** |
| reconcile `readback_capture` | 3967 | 11 901 | **12 000** |
| reconcile `reset_script` | *(no sample on 717a3c1 path)* | — | **28 000** (reconcile analogue) |
| reconcile `post_reset_readback` | *(path not taken)* | 11 901 | **12 000** |
| **merged apply / reassert tail** (single PS process) | **7291** (reconcile_end − reconcile_steps_total) | 21 873 | **22 000** |

**Apply pipe path (merged, one call + one kill wait):**

| | ms |
|--|--|
| PowerShell budget | 22 000 |
| Kill wait | 3 000 |
| **Apply worst-case total** | **25 000** (< 30 000 client IO) |

**Reconcile wall (5 PS calls + kill waits + margin):**

| | ms |
|--|--|
| Budget Σ | 13 000 + 12 000 + 28 000 + 12 000 + 22 000 = **87 000** |
| + kill waits (5 × 3 000) | **15 000** |
| Planned worst-case | **102 000** |
| + margin | **10 000** |
| vs `ReconcileWallTimeoutMs` | **112 000 ≤ 120 000** (headroom **8 000**) |

Implementation: `InternetFirewallMergedApplyRunner` — one encoded PowerShell per `Apply(on/off)` (`apply_merged_on` / `apply_merged_off` in watchdog); reassert tail uses the same merged `Apply`. Core constants: `GuardInternetBudgetDefaults.ApplyMergedPowerShellBudgetMs`, `ApplyPathPowerShellCallCountWorstCase = 1`.

---

## (2) 180 s hang — proved root cause (product STOP)

**Test:** `live_server_pc_internet_guard_forced_apply_failure_shows_error_and_offline_at_press`  
**Run:** 44567958282, job 135714792452, head 717a3c1.

### Timeline (UTC)

| Time | Event |
|------|--------|
| 16:57:53 | `START dotnet-test-net48-live-checkpoint` |
| 16:58:03 | Live DB reset harness done |
| 16:58:05.790 | `L17_PROBE_GUARD_STARTED pid=6944` → next line in test: `WaitForPipeReadyAsync` → `GuardInternetClient.GetStatusAsync` |
| 16:58:06–16:58:21 | Guard watchdog: `reconcile_start` … `reconcile_end completed=true elapsed_ms=15437` (pipe accept loop live) |
| 16:58:22–17:00:52 | CI heartbeats every 30 s — **no** `L17_PROBE_PIPE_WAIT` / `L17_PROBE_PIPE_READY` (probe blocked inside **first** `GetStatusAsync`, not iterating) |
| 17:01:04.965 | xUnit **Test execution timed out after 180000 ms** (~179 s after guard start) |

### Which call blocked

- **Blocked call:** `GuardInternetClient.GetStatusAsync` → `SendAsync` (connect + write + read), first attempt inside `L17GuardProbeSupport.WaitForPipeReadyAsync`.
- **Client timeouts configured:** connect **5 000 ms**, IO **30 000 ms** (`DefaultConnectTimeoutMs` / `DefaultIoTimeoutMs`) — neither fired over the hung window → thread blocked in server-side read, not client timeout.
- **Guard pipe state at 16:58:21:** reconcile finished; listener thread accepting (`reconcile_end`); handler stalled.

### Root cause (product, not applied)

`InternetPipeServer.HandleConnectedClient` uses `StreamReader.ReadToEnd()` before writing the response. `GuardInternetClient` keeps the pipe open until after it reads the response (dispose at end of `SendAsync`). **Full-duplex deadlock:** server waits for client end-of-stream; client waits for server response. Explains a single hung `GetStatusAsync` past the 90 s probe loop wall and up to the 180 s Fact timeout.

**STOP — minimal diff (do not apply in R18c scope 1/3/4 only):**

```diff
--- a/src/GameClub.Kiosk.Guard/Internet/InternetPipeServer.cs
+++ b/src/GameClub.Kiosk.Guard/Internet/InternetPipeServer.cs
@@ -111,7 +111,15 @@
                 using var reader = new StreamReader(server, Encoding.UTF8, false, 8192, leaveOpen: true);
                 using var writer = new StreamWriter(server, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };
-                var body = reader.ReadToEnd();
+                var buffer = new char[8192];
+                var read = reader.Read(buffer, 0, buffer.Length);
+                if (read <= 0)
+                {
+                    return;
+                }
+
+                var body = new string(buffer, 0, read);
                 var response = HandleRequest(body);
```

(File:line `InternetPipeServer.cs:114`.)

---

## (5) `within_one_tick` — PR #40 vs PR #42

### CI evidence

| Run | Branch | Symptom | Sessions |
|-----|--------|---------|----------|
| 44567958282 | PR #40 | `connecting…`, `runtimeState=Playing`, `shellPhase=Locked`, session ids **set**, top-up 200 / credential 204 | Bound |
| 44571842885 | PR #42 | `Locked — … · connected`, `runtimeState=Locked`, `shellPhase=Locked`, **session ids empty** | Never bound |

PR #42 failure is **not** explained by L17/orphan poison (no Guard internet probe on that branch).

### Code paths

- **`shellPhase`:** `KioskShellOrchestrator.OnHeartbeatAck` (`KioskShellOrchestrator.cs:60–94`) — `Playing` only when `SessionStatus == Open`, host `sessionId != Empty`, lease verifies, and lease session matches host session (`:72–76` else `Locked`).
- **`runtimeState`:** `KioskRuntime.State` → `Playing` on `BindSession` / ack path (`KioskRuntime.cs:208`, `:433`).
- **`LoginAsync`:** `KioskHostService.cs:177–204` — HTTP login, `BindSession`, **`ShowPlayingLayout`**, but **does not** call `OnHeartbeatAck`; orchestrator can stay `Locked` until first qualifying heartbeat.
- **Status `connecting…`:** `KioskStationStatus.Resolve` (`KioskStationStatus.cs:36–84`) — with open session and transport up but **no** `estimatedRemainingSeconds` on display, execution falls through to default `connecting…` (line 82–83).

**PR #40 mismatch:** login pushes **runtime `Playing` + playing layout** while orchestrator remains **`Locked`** until heartbeat; status stays **`connecting…`** without `Display.EstimatedRemainingSeconds` from heartbeat — customer sees a locked/connecting shell though login succeeded.

**PR #42 mismatch:** login never retained `hostSessionId` / `runtimeSessionId` (`Guid.Empty`) — failure before/during bind or cleanup, distinct from PR #40.

### Main CI — `wpf remaining time stability (20 runs)`

| Run ID | Date | Step 9 result |
|--------|------|----------------|
| 44276759016 | 2026-11-18 | **success** (20/20) |
| 44061140758 | 2026-11-17 | **success** |
| 44002537356 | 2026-11-17 | **success** |
| 43610135397 | 2026-11-14 | **success** |

(Step absent on older/shorter main workflows before `windows-2022 kiosk integration` carried WPF stability.)

### STOP (product, not applied)

**A — login/orchestrator sync (PR #40 symptom):** After successful login, host should not show playing/runtime `Playing` until orchestrator accepts the lease, or `LoginAsync` should prime orchestrator consistently.

```diff
--- a/src/GameClub.Kiosk/Services/KioskHostService.cs
+++ b/src/GameClub.Kiosk/Services/KioskHostService.cs
@@ -196,7 +196,6 @@
         _locallyLockedSessionId = Guid.Empty;
         _runtime.BindSession(_sessionId);
         await ApplyStationProfileAsync();
-        ShowPlayingLayout();
         _uiTick.Start();
```

Defer `ShowPlayingLayout()` to existing `OnHeartbeatAcknowledged` branch when `phase == Playing` and session open (`~385–397`).

**B — optional:** ensure first post-login heartbeat includes `Display.EstimatedRemainingSeconds` (server `KioskDisplayService` — out of scope; do not touch `KioskServices.cs:262–265`).

**PR #42** needs stability-run server/kiosk logs (artifact now uploaded); empty session points at login/bind race — STOP until logs confirm server vs host.

---

## STOPs summary

| Issue | File:line | Applied? |
|-------|-----------|----------|
| Pipe probe 180 s deadlock | `InternetPipeServer.cs:114` | **No** — report only |
| WPF playing/locked mismatch | `KioskHostService.cs:199` vs `KioskShellOrchestrator.cs:60–94` | **No** — report only |

---

## Verification

- Release build gate (5 projects, `EnableWindowsTargeting=true`): **0 errors**
- `PcInternet` Core tests (net10): **23 passed**