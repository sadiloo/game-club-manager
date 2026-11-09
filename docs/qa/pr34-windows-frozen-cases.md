# PR #34 — `windows-2022 kiosk integration` honesty table

Job name: **`windows-2022 kiosk integration`** (`.github/workflows/ci.yml`).

Artifacts (uploaded with `if: always()`): `kiosk-windows-integration` — `kiosk-windows-guard.trx`, `watchdog.log`, `evidence/**` (includes `screenshot-lock.png` when WPF STA render succeeds). KioskRuntime + server proofs run on **ubuntu net10** (`GameClub.Kiosk.Windows.IntegrationTests` net10.0) with the Linux postgres service on the same commit.

| Case | Proved on runner | NOT PROVED (reason) |
|------|------------------|---------------------|
| 1 | net48 build; SignalR transport via `KioskRuntime` against in-process `MutableClock` server host | Win 7 SP1 / 8.1 / Home matrix; SSE-only on 7 |
| 2 | Partial: WPF smoke (process start or STA screenshot when session allows) | Full gpedit / Home policy matrix across editions |
| 3 | F6 five failures + operator credential reset + funded login (server integration test) | Full dual-PC WPF race |
| 4 | Guest session paths not exercised in this job | Full guest prepaid UI |
| 5 | Heartbeat transport connect on paired device | Next-day retention UI |
| 6 | Server-backed estimate bands remain on Linux CI | Wall-clock skew warning on Windows UI |
| 7 | Dead PC 600 s (10 min) auto-close via `MutableClock` + internal sweep | Full shift attribution on Windows shell |
| 8 | Category catalog FK covered on Linux; SQLite merge in Core tests | USB / file-dialog escape on WPF |
| 9 | Allowlist exact-match rules in Core tests (Linux) | Steam/Epic/D: chains on runner |
| 10 | G1 (a–e) registry tamper/repair on real `Registry.Users` hive; `guard.policy_tamper` in `watchdog.log`; G2 DACL read-back after `SetKernelObjectSecurity` | Non-admin terminate unless `CHECKPOINT_SECOND_USER` set; Ctrl+Alt+Del |
| 11 | 5 s heartbeat loop in `KioskRuntime` against live hub | Full kill/restart durability matrix |
| 12 | Disconnect = dispose hub + reconnect test | Powered-offline lock UX on WPF |
| 13 | Dead PC sweep at T+10 min checkpoint gap (case 13 server rule) | Physical power-off |
| 14 | Logout/hold not driven from WPF in this job | Full PWA hold/cancel |
| 15 | Station target lock on Linux server tests | Dual-PC WPF hop |
