# PR #40 R19 / R19b report

Branches: `cursor/pr40-r19-staging` (`5af88ae`), `cursor/pr40-r19b-staging` (NDJSON framing).

## (4) Literal timeline — run **44606002155** (`5af88ae98905836539f6a6c8c92a17a5fbf50209`)

| Field | Value |
|-------|-------|
| **Run id** | [44606002155](https://github.com/sadiloo/game-club-manager/actions/runs/44606002155) |
| **Windows job id** | `135849731601` (`windows-2022 kiosk integration`, conclusion: **failure**) |
| **Artifact id** | `14573017605` (`kiosk-windows-integration`) |
| **L17 artifact files** | `watchdog.log`, `kiosk-windows-logs/live-checkpoint-net48-console.log` |

### L17 attempt 1 — correlated Guard + client (same connection window)

| UTC (log timestamp) | Source (artifact / job log) | Literal line |
|---------------------|----------------------------|--------------|
| 2026-11-19T20:31:55.6726369Z | `watchdog.log` | `pipe_accept thread=3` |
| 2026-11-19T20:31:55.6726369Z | `watchdog.log` | `pipe_client_handler_begin thread=9` |
| *(none in artifact)* | `watchdog.log` | **No** `pipe_read_begin` / **no** `pipe_read_ok` before later accepts |
| 2026-11-19T20:31:55.265Z (`utc_ms=1795120315265`) | `live-checkpoint-net48-console.log` | `GUARD_PIPE … verb=status event=start` |
| 2026-11-19T20:31:55.672Z (`utc_ms=1795120315672`) | `live-checkpoint-net48-console.log` | `GUARD_PIPE … verb=status event=connect_ok thread=6` |
| 2026-11-19T20:31:55.672Z (`utc_ms=1795120315672`) | `live-checkpoint-net48-console.log` | `GUARD_PIPE … verb=status event=write_begin thread=6` |
| *(absent ~30s)* | `live-checkpoint-net48-console.log` | **No** `write_ok` / `flush_ok` / `read_begin` |
| 2026-11-19T20:32:25.673Z (`utc_ms=1795120345673`) | `live-checkpoint-net48-console.log` | `GUARD_PIPE … verb=status event=error_pipe_io_timeout thread=10` |
| 2026-11-19T20:32:25.673Z (`utc_ms=1795120345673`) | `live-checkpoint-net48-console.log` | `GUARD_PIPE … verb=status event=dispose_begin` / `dispose_ok` |

Attempts 2–3: same pattern (`connect_ok` → `write_begin` → ~30s → `pipe_io_timeout`, no `write_ok`); job log step `dotnet-test-net48-live-checkpoint` echoes console at 2026-11-19T20:33:35Z.

### Later watchdog accepts (still no `pipe_read_ok`)

| UTC | `watchdog.log` |
|-----|----------------|
| 2026-11-19T20:32:26.1854677Z | `pipe_accept thread=3` / `pipe_client_handler_begin thread=15` |
| 2026-11-19T20:32:56.7089116Z | `pipe_accept thread=3` / `pipe_client_handler_begin thread=16` |

**Conclusion (44606002155):** Guard accepted and entered the per-connection handler **without any `pipe_read_ok` in the captured `watchdog.log`**, while the client reached `connect_ok` and stalled on **`write_begin` for ~30s** then `pipe_io_timeout`. That matches **`ReadToEnd()` waiting for EOF** on the pre-framing Guard (`InternetPipeServer.cs:119` on `5af88ae`) while the client kept the duplex pipe open — not a gate-before-read failure. The missing `pipe_read_begin` lines in this artifact are unexplained vs `5af88ae` source (handler logs `read_begin` before `ReadToEnd`); treat as incomplete watchdog capture, not refutation of the client-side hang.

---

## ReadToEnd root cause (pre-R19b)

Pre-framing Guard used `StreamReader.ReadToEnd()`; client sent unframed JSON and waited for a response without half-closing the write side → deadlock class above.

## NDJSON framing (R19b)

One JSON object per line; shared byte-safe reader `GuardInternetPipeLineReader` (UTF-8 `MemoryStream`, 64KiB cap); client `StreamReader.ReadLine` under IO timeout (net48: `Task.Delay` race).

## Tests

`internet_pipe_utf8_persian_field_round_trips_on_real_pipe` — net10 (stub echo) + net48 (`InternetPipeServer`, Persian `StationId` apply).

## Run ids

| Run | Head | Note |
|-----|------|------|
| **44606002155** | `5af88ae` | Literal evidence table above |
| **44598481278** | R18e | Prior `write_begin` hang reference |