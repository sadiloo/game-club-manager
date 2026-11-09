# PR #34 — frozen cases 1–15 Windows vs Linux proof

Job name: **`windows-2022 kiosk integration`** (`.github/workflows/ci.yml`).

Artifacts (uploaded with `if: always()`): `kiosk-windows-integration` — `kiosk-windows-server.trx`, `kiosk-windows-guard.trx`, `watchdog.log`, `evidence/**` (includes `screenshot-lock.png` when WPF STA render succeeds). The job log also prints trx summaries, `watchdog.log`, evidence files, and screenshot metadata (`print kiosk integration evidence`).

Live server: PostgreSQL 16 on the runner, out-of-process `GameClub.Server` (`CHECKPOINT_SERVER_URL`). KioskRuntime proofs use **real** SignalR on Windows (no `testHubHandler` when the live URL is set). The 600 s dead-PC case backdates `sessions.LastCheckpointUtc` in the test database, then calls the existing `POST /api/internal/dead-pc-sweep` (no production clock switch).

| Case | Windows proof (test name + job) | Linux proof | NOT PROVED |
|------|----------------------------------|-------------|------------|
| 1 | net48/net10 build; `live_server_net48_transport_heartbeat_rows` / server live tests on `windows-2022 kiosk integration` | Core + net10 factory integration | Win 7 SP1 / 8.1 / Home matrix; SSE on 7 |
| 2 | `wpf_lock_screen_and_remaining_time_visible` | — | Full test/final/recovery matrix |
| 3 | `live_server_pair_f6_lock_and_heartbeat_rows` | `KioskFixR1` F6 server tests | Dual-PC WPF race |
| 4 | — | Server guest tests | Guest paths on WPF/Win |
| 5 | WPF lock/status + display fields in smoke | Billing/server estimate tests | Multi-day retention UI |
| 6 | — | Band/discount server tests | Wall-clock skew on WPF |
| 7 | Grace/burnt on Billing CI (unchanged) | Same | Full shift on shell |
| 8 | `catalog_put_unknown_category_returns_400` + operator catalog on server CI | SQLite merge Core tests | USB/file-dialog on WPF |
| 9 | `allowlist_blocks_cmd_allows_configured_game_process` | Core allowlist unit tests | Steam/Epic chains on runner |
| 10 | G1 registry + `g1_tamper_triggers_guard_policy_tamper_log_line` + `g2_acl_on_guard_and_supervised_shell_after_launch` + watchdog + `g2_non_admin_terminate_attempt_reports_honestly` (DACL + terminate probe use **CheckpointKiosk** / **CheckpointGuest** SIDs; `CHECKPOINT_KIOSK_USER` is runner only so Guard can launch in the logged-on session) | Core G1/G2 seam | Ctrl+Alt+Del; reboot; Win7 Home gpedit; supervised shell as real interactive **CheckpointKiosk** logon |
| 11 | `live_server_*` heartbeat + disconnect/reconnect | net10 factory on ubuntu | Full kill matrix |
| 12 | disconnect + dead-PC sweep with DB backdate | ubuntu factory | Powered-offline WPF UX |
| 13 | `live_server_dead_pc_sweep_after_checkpoint_gap` | `PoClarification` acceptance | Physical power-off |
| 14 | `session_end_clears_launcher_cache_paths` exercises `DeleteTreeContents` on **Downloads** only | — | PWA hold/cancel; on Windows: `AppData\Local\Temp`, `INetCache`, Edge/Chrome/Firefox cache paths under kiosk profile (**NOT PROVED** — runner lacks writable kiosk-profile trees / ACL on some caches) |
| 15 | — | SS-4 server tests | Dual-PC hop on WPF |

Shell launch/watchdog tests use the **runner logged-on session** (`CHECKPOINT_KIOSK_USER` = runner; `CHECKPOINT_GUARD_CI_RUNNER_LAUNCH=1` when `WTSQueryUserToken` is unavailable on GHA) while **CheckpointKiosk** SID is on the DACL; see case 10 NOT PROVED for true kiosk interactive session.
