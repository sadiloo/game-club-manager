# PR #34 — Windows frozen-case honesty (checkpoint MVP)

Windows proof lists **only** tests that run and pass on `windows-2022` kiosk integration for this PR branch. Linux proofs stay separate.

| Case | Windows proof (honest) | Linux proof | NOT PROVED |
|------|------------------------|-------------|------------|
| **1** | net48 build on `windows-2022 kiosk integration`; `live_server_net48_transport_heartbeat_rows` (Server SKU transport + ≥3 heartbeat rows). **Not** Win 7/8.1/Home runtime matrix. | Core + net10 factory | Win 7 SP1 / 8.1 / Home; SSE-on-7 on pilot hardware |
| **2** | `wpf_lock_screen_and_remaining_time_visible` (lock visible); `wpf_remaining_time_matches_funded_session_within_one_tick` (server session @ 80,000/h, wallet 48,667 → 2,160 s on StatusText within 1 UI tick; test does not write display fields) | — | Test/final/recovery matrix; inspectable screenshot in CI logs |
| **3** | `live_server_pair_f6_lock_and_heartbeat_rows` (HTTP 403 + DB lock); `live_server_pairing_registers_device_on_station`. **Not** kiosk lock screen after 5 failures | F6 server tests | 2-PC race; WPF locked UI |
| **4** | — | Guest server tests | Guest paths on WPF/Win |
| **5** | `wpf_remaining_time_matches_funded_session_within_one_tick` (playing StatusText from server ack) | Billing estimate / server display tests | Multi-day package retention UI |
| **6** | — | Bands/discount server + Billing on ubuntu job | Wall-clock skew on WPF |
| **7** | — | Billing + server; B2 **held** | Full grace/burnt on shell/shift |
| **8** | — | `catalog_put_unknown_category_returns_400` on **ubuntu net10 only** | USB/file-dialog on WPF |
| **9** | `allowlist_terminates_real_denied_process_and_preserves_allowed` (production `KillDeniedProcesses` / `EnforceAllowlist` invoked by test; real cmd + copied long-running exe + notepad) | Core allowlist unit + predicate tests | Steam/Epic/anti-cheat chains on runner |
| **10** | G1 registry tests + tamper log + `g2_acl_on_guard_and_supervised_shell_after_launch` + post-kill `guard_watchdog_relaunches_killed_shell_logged` + non-admin probe (`CHECKPOINT_GUARD_CI_RUNNER_LAUNCH`) | Core G1/G2 seam | Ctrl+Alt+Del; reboot; Task Manager; Sticky Keys; WTSQueryUserToken production path; true CheckpointKiosk interactive logon |
| **11** | ≥3 heartbeat rows; `live_server_z_disconnect_reconnect_same_session_no_double_debit` (kill `GAMECLUB_SERVER_PID`, ≥12 s outage, queue drain; not dispose/start runtime) | net10 factory + outage helper | Full kill/replay matrix cadence proofs |
| **12** | Same disconnect test as case 11 (server stop + SQLite queue + reconnect). **Not** cable-pull UX or F1 after 10 min on WPF | ubuntu factory | Cable-pull semantics; open-guest elapsed-only on WPF |
| **13** | `live_server_dead_pc_sweep_after_checkpoint_gap` (**DB backdate + manual sweep**, 80,000 collectible). **Not** physical power-off or controlled production clock | PoClarification acceptance | Physical power-off; restart-before-timeout on real clock |
| **14** | `session_end_clears_launcher_cache_paths` (**Downloads only**) | — | PWA hold/cancel; INetCache/Temp/browser caches on kiosk profile |
| **15** | — | SS-4 server | Dual-PC hop on WPF |

## Leftovers (explicit)

- F6 **kiosk UI** locked state after 5 failed attempts  
- Downloads-only cleanup (full profile cache paths)  
- **WTSQueryUserToken** production shell launch path  
- Win **7 / 8.1 / Home** matrix  
- Physical **power-off** and **reboot**  
- **Ctrl+Alt+Del** / secure attention / Task Manager escape  
- Two-PC session race; Steam/Epic/anti-cheat on runner  
- **Kiosk on-screen disconnected indicator:** none / not proved (B6: during network cut the screen shows only time; outage proof is `IsTransportConnected` on the runtime)
