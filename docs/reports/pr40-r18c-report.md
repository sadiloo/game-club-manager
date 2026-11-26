# PR #40 R18c report

## Staging

| Field | Value |
|-------|--------|
| **Branch** | `cursor/pr40-r18c-staging` (fast-forward only) |
| **Parent** | `717a3c1ae499d648464530975db3799f6f85722a` |
| **Code sha** | `a219008` |
| **Docs sha** | *(this commit)* |

## Item status

| # | Status |
|---|--------|
| 1 | Client error mapping — **done** (prior commit) |
| 2 | 180 s hang — **revised diagnosis below (product STOP, not applied)** |
| 3 | Merged apply budgets — **done** (gate on `a219008`) |
| 4 | Orphan harness — **done** (prior commit) |
| 5 | `within_one_tick` — **revised diagnosis below (product STOP, not applied)** |

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

---

## A. Pipe 180 s — revised diagnosis (contradictions resolved)

### A.1 Which client/code does L17 use?

| Step | Location |
|------|----------|
| Test | `WindowsPcInternetApplyFailureLiveTests.cs:129` → `L17GuardProbeSupport.WaitForPipeReadyAsync` |
| Wait loop | `L17GuardProbeSupport.cs:35–45` → `new GuardInternetClient().GetStatusAsync(ct)` |
| Transport | **`GuardInternetClient.SendAsync`** — `src/GameClub.Kiosk.Core/Guard/GuardInternetClient.cs:26–63` |

There is **no** separate test-side pipe client. L17 net48 live checkpoint uses the **same** `GuardInternetClient` as Core/integration tests.

**Timeouts on that path (717a3c1):**

- Connect: `GuardInternetClient.cs:47–49` — `pipe.Connect(connectMs)` synchronous on **net48** (default **5 000 ms**).
- Write/flush/read: `WriteWithTimeoutAsync` / `FlushWithTimeoutAsync` / **`ReadWithTimeoutAsync` :106–122** — races `pipe.ReadAsync` against **`Task.Delay(ioMs)`** (default **30 000 ms**); on loss throws `TimeoutException` → mapped to `pipe_io_timeout`.

`WaitForPipeReadyAsync` only catches `TimeoutException` and logs `L17_PROBE_PIPE_WAIT` (`L17GuardProbeSupport.cs:52–58`).

**Conclusion (1):** The shared client **does** have IO timeout logic; a **pure** ReadToEnd deadlock on the server should still produce **`pipe_io_timeout` within ~30 s per attempt**, not a silent **~179 s** stall unless:

- the hang is **not** inside `ReadWithTimeoutAsync` (e.g. **before** first status attempt returns, or **outside** `GetStatusAsync`), or
- stdout never flushed (717a3c1 had no `Console.Out.Flush` on probe waits), or
- connect/read throws a **non-`TimeoutException`** that aborts the wait loop differently (not seen in log).

### A.2 ReadToEnd vs earlier green pipe round-trips

`InternetPipeServer.HandleConnectedClient` **`StreamReader.ReadToEnd()`** at `InternetPipeServer.cs:114` dates to early pipe work (e.g. 18d72ca); **the same server code** is used by:

- In-proc harness / integration (`Category=PcInternetFirewall`) — e.g. `internet_pipe_status_ready_within_2s_while_reconcile_slow` — **passes on CI**.
- Live server tests with **harness** `InternetPipeServer` thread — e.g. run **44567958282** **`live_server_pc_internet_toggle_off_report_applied_then_session_end_on` passed at 16:53:58** (7 s) on the **same job** as the L17 timeout (client `GuardInternetClient` + in-proc pipe, not `GameClub.Kiosk.Guard.exe`).

**Earlier L17 success evidence (harness/docs, not 717a3c1 console):**

- **R13 report** (`docs/reports/pr40-r13-report.md`): expects `L17_GUARD_OFF_PROBE ... blocked=True guard_rule_count>=1 state_present=True` on green live runs.
- **R17 report** (`docs/reports/pr40-r17-report.md`, run **44545653691**): `L17_PROBE_PIPE_WAIT` attempts every ~5 s then connect failure — **not** a 179 s silent stall; **no** `L17_GUARD_OFF_PROBE` (failed before apply).

**Difference L17 real Guard (717a3c1) vs green paths:**

| | Green paths | 717a3c1 L17 hang |
|--|-------------|------------------|
| Server | In-proc `InternetPipeServer` or harness thread | **`GameClub.Kiosk.Guard.exe`** (`GuardProcessStart.cs:36`) |
| Listener | Up immediately (R18+) | Watchdog shows reconcile **15.4 s**; listener should accept during reconcile |
| Logs | `L17_PROBE_PIPE_*` or `L17_GUARD_OFF_PROBE` | **Only** `L17_PROBE_GUARD_STARTED`; **no** pipe wait/ready lines |

**If ReadToEnd always blocked until client close with no timeout:** every in-proc status call would hit **30 s `pipe_io_timeout`** and retry — tests would not be green at 2 s. So **ReadToEnd + this client does not behave as “always infinite” on CI**; dispose/timeout/unblock allows round-trips in harness runs.

**Explicit statement:** A **strict** always-deadlock ReadToEnd model **is inconsistent** with (a) sub-2s pipe integration tests and (b) **7 s** live toggle-off apply on run **44567958282**. The **179 s** L17 stall therefore **cannot be proven** as ReadToEnd-only deadlock without another mechanism (e.g. hang **outside** timed `GetStatusAsync`, or **no pipe wait lines captured**).

**Funded/apply live tests** on that run **before** L17: net10 server internet toggles and kiosk runtime paths **passed** with harness pipe — they do **not** prove real-Guard probe; they prove **client + in-proc server** round-trip under CI.

### A.3 Framing — single `Read(8192)` is unsafe; proposed protocol

Current request is single-line JSON without newline (`GetStatusAsync` serializes compact JSON). **`Read(8192)` once** can truncate or miss split writes.

**Proposed framing (newline-delimited JSON, both sides):**

```diff
--- a/src/GameClub.Kiosk.Core/Guard/GuardInternetClient.cs
+++ b/src/GameClub.Kiosk.Core/Guard/GuardInternetClient.cs
@@ -19,7 +19,7 @@ public sealed class GuardInternetClient
     public Task<string> GetStatusAsync(CancellationToken ct = default)
-        => SendAsync(JsonSerializer.Serialize(new { verb = "status" }), ct);
+        => SendAsync(JsonSerializer.Serialize(new { verb = "status" }) + "\n", ct);
 
     private static async Task<string> SendAsync(string requestJson, CancellationToken ct)
@@ -58,8 +58,12 @@ public sealed class GuardInternetClient
         await WriteWithTimeoutAsync(pipe, req, 0, req.Length, ioMs, ct).ConfigureAwait(false);
         await FlushWithTimeoutAsync(pipe, ioMs, ct).ConfigureAwait(false);
-        var buf = new byte[16_384];
-        var read = await ReadWithTimeoutAsync(pipe, buf, 0, buf.Length, ioMs, ct).ConfigureAwait(false);
-        return Encoding.UTF8.GetString(buf, 0, read);
+        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
+        var lineTask = reader.ReadLineAsync();
+        var completed = await Task.WhenAny(lineTask, Task.Delay(ioMs, ct)).ConfigureAwait(false);
+        if (completed != lineTask)
+            throw new TimeoutException(GuardInternetClientErrorCodes.IoTimeout);
+        return await lineTask.ConfigureAwait(false) ?? string.Empty;
     }

--- a/src/GameClub.Kiosk.Guard/Internet/InternetPipeServer.cs
+++ b/src/GameClub.Kiosk.Guard/InternetPipeServer.cs
@@ -111,8 +111,8 @@ internal sealed class InternetPipeServer
             {
                 using var reader = new StreamReader(server, Encoding.UTF8, false, 8192, leaveOpen: true);
                 using var writer = new StreamWriter(server, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };
-                var body = reader.ReadToEnd();
-                var response = HandleRequest(body);
-                writer.Write(response);
+                var body = reader.ReadLine() ?? string.Empty;
+                var response = HandleRequest(body);
+                writer.WriteLine(response);
             }
```

**Product STOP (pipe):** Do **not** apply single-read patch; use **NDJSON / ReadLine** if product change is approved. **179 s root cause remains unproven** as ReadToEnd deadlock alone; **recommended next CI artifact:** flush probe logs + per-attempt timing in harness to separate connect vs read vs post-ready `L17RealInternetOffProbe` hub wait.

---

## B. `within_one_tick` PR #40 (run 44567958282, stability **run 1/20**, ~17:07–17:09)

**Diagnostics (job log):** `runtimeState=Playing`, `shellPhase=Locked`, `connecting…`, **sessions bound** (`7d0d7f15-…`), transport **Connected**, setup HTTP 200/204.

**This is not explained by deferring `ShowPlayingLayout` alone.**

### Code: why `Locked` vs `Playing` vs `connecting…`

| Signal | Mechanism |
|--------|-----------|
| `runtimeState=Playing` | `KioskHostService.LoginAsync` → `BindSession` → `KioskRuntime.BindSession` :221–226 sets **Playing** when transport up |
| `shellPhase=Locked` | `KioskShellOrchestrator.OnHeartbeatAck` :60–94 — **Playing** only if `SessionStatus=="Open"`, host `sessionId!=Empty`, lease verifies, and **lease session matches host** (:72–76); else **Locked** at :93 |
| `connecting…` | `KioskStationStatus.Resolve` :36–55 — with `hasOpenSession` and transport up, if **`ack.Display.EstimatedRemainingSeconds` is null**, no `left` line; falls through to :80–83 **connecting…** |

### Which `OnHeartbeatAck` condition failed for ~90 s?

**JOB CONSOLE HAS NO `LEASE_VERIFY` / heartbeat ack dump for this WPF stability run** (unlike net10 server tests at 16:54:49). From **phase == Locked** (not `LeaseExpired`) and bound host session:

| Condition | Likelihood |
|-----------|------------|
| **`ack.SessionStatus != "Open"`** for heartbeats while polling | **High** — default path :93 sets **Locked** |
| **`ack.Lease.Payload.SessionId != _sessionId`** (:72–76) | **Medium** — forces **Locked** |
| **`!VerifyLease(ack.Lease)`** | **Low** for **Locked** — would set **LeaseExpired** :82 |

**Cannot prove which of the first two without `wpf-stability-server-*.log` ack payloads** (artifact referenced but not in captured job text).

### Orphan Guard/Kiosk after L17 (pid **6944**, leak at 17:03:18)

- **SignalR / HTTP 5225:** diagnostics show **Connected** — orphans **did not** block transport.
- **Named pipe `CheckpointGuardInternet`:** irrelevant to WPF stability test (no Guard client in `within_one_tick`).
- **Indirect poison:** possible shared `ProgramData\Checkpoint\kiosk\device.json` / firewall state; **not proven** in log.

### Real cause (evidence-bounded)

1. **Test failure line:** missing **`~hh:mm:ss left`** because heartbeats lacked **`Display.EstimatedRemainingSeconds`** while status logic still shows **connecting…** (`KioskStationStatus.cs:41–55`, `80–83`).
2. **Orchestrator stayed Locked** because heartbeats for ~90 s **did not satisfy the Open+lease Playing branch** (`KioskShellOrchestrator.cs:70–93) — **most likely `SessionStatus != "Open"` or lease session mismatch**; **not** disproved by deferring `ShowPlayingLayout`.

**Minimal product diff (if PO approves, not applied):** ensure post-login kiosk heartbeats return **`SessionStatus=Open`**, **lease `SessionId` == login session**, and **non-null `Display.EstimatedRemainingSeconds`** for funded customer — investigate server heartbeat assembly for stability TestHost (harness logs required to cite `file:line`).

**Not applied:** remove-only `ShowPlayingLayout` from `LoginAsync` — **insufficient** for this failure mode.

---

## C. PR #42 empty session (run **44571842885**, job **135728722778**, stability **run 5/20**, 17:16:13–17:17:49)

**Diagnostics:** `hostSessionId` / `runtimeSessionId` **empty**; `runtimeState=Locked`; status **`Locked — PC-WPF-b446ba53 · connected`**; setup **top-up 200**, **credential 204**.

**Login call in captured job log:**

- The only `POST .../api/kiosk/auth/login` **200** in the published job tail is **`17:17:51.974`** (during artifact **re-print**, after failure at **17:17:49**), **not** tied to station `b446ba53` in that line.
- **No** run-5 console line `Login failed`, **no** failed HTTP status for kiosk login during 17:16–17:17 in the excerpt.

**Conclusion (log-bound):**

- Operator **setup succeeded** (200/204).
- **Kiosk `LoginAsync` did not bind** `_sessionId` / runtime session (diagnostics **Guid.Empty**).
- Logs **do not show** whether **`/api/kiosk/auth/login` failed** for run 5 or was **never invoked successfully** — need **`wpf-stability-server-out.log` / `wpf-stability-run-5.log`** (run 5 failed before artifact upload was added on 717a3c1; PR #42 may lack per-run log in console).
- **Not L17/orphan** on this branch (no probe); runs **1–4 passed** in **8 s** each on same stability server → **flake on run 5**, likely **login/bind or device.json/station mismatch** for generated login name — **product/harness STOP pending artifact**.

---

## STOPs (unchanged policy)

| Issue | Action |
|-------|--------|
| Pipe 179 s | **Unproven** as ReadToEnd-only; NDJSON framing diff proposed; no product apply in R18c scope |
| WPF PR #40 | **Open/lease/display** on heartbeat — needs stability server logs for server `file:line` |
| WPF PR #42 | **Login bind** — needs run-5 server/kiosk log artifact |

---

## Verification (code sha `a219008`)

Release build gate (5 projects): 0 errors. PcInternet Core: 23 passed.