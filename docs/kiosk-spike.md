# Kiosk spike — PC shell for a gaming cafe

Prototype of the Windows PC client for a گیم‌نت (PC and console). The billing engine prices the session. This spike is the locked-down shell, the guard service, and the offline log. There is no cafe server yet: the shell talks to an in-memory stand-in, and a SignalR client is ready for the ASP.NET Core hub.

## Who runs what

| Machine | Minimum OS | Runtime |
| --- | --- | --- |
| Cafe server (future ASP.NET Core + SignalR) | Windows 10 64-bit, or Windows Server | .NET 10 |
| Each gaming PC: shell and guard | Windows 7 SP1, Windows 8.1, Windows 10, Windows 11. Home and Pro | .NET Framework 4.8 |

.NET 10 does not run on Windows 7 or 8.1, so the shell and the guard are **net48** WPF / Windows Service projects, C# `LangVersion` latest. Shared decisions (session clock, catalog, block list, offline log, pipe frames, transport choice) live in `GameClub.Kiosk.Core`, multi-targeted **net48 and net10.0**, and are tested with xUnit on Linux. `GameClub.Billing` is multi-targeted **netstandard2.0 and net10.0** so the same pricing code runs inside the net48 shell. The server stays on .NET 10 and keeps referencing the net10.0 build.

Windows 8 (the original 6.2 release, not 8.1) cannot install .NET Framework 4.8. Those PCs must be upgraded to 8.1 before this client is installed. The installer exits with that message, and the shell refuses to log anyone in if it still detects Windows 8.

Shell Launcher, Assigned Access, WinRT, and toast notifications are not used. Shell Launcher exists only on Enterprise, Education, and IoT. Home has no `gpedit.msc`. Lockdown is the per-user Winlogon shell plus registry policy values that have existed since Windows 7 and that Home editions honor without Group Policy.

## Layout

```text
src/GameClub.Billing/            netstandard2.0 + net10.0   pricing (existing engine)
src/GameClub.Kiosk.Core/         net48 + net10.0            testable kiosk logic
src/GameClub.Kiosk/              net48 WPF                  Persian RTL shell
src/GameClub.Kiosk.Guard/        net48 Windows service      restart + process guard
tests/GameClub.Kiosk.Core.Tests/ net10.0 xUnit              runs on Linux
scripts/install-kiosk.cmd        Windows 7+ installer (reg.exe, no PowerShell required)
scripts/install-kiosk.ps1        same installer, PowerShell 2.0
scripts/uninstall-kiosk.cmd
scripts/uninstall-kiosk.ps1
```

Build check used here: `dotnet build` of the solution, including the net48 WPF and service projects, with `EnableWindowsTargeting`. WPF was not executed. Windows behavior below that is not covered by xUnit is called out as a manual test.

## Session clock

After login the shell prices one continuous segment with `SessionPricer` and settles it with `CustomerPlay.Settle` against the account snapshot from login.

- Money on screen is the spendable PC toman (never a negative number). Time is the covered end minus now, at the station's hourly rate.
- The default billing rule applies: if the balance is still above zero but cannot pay the next minute, that one minute is granted. The shortfall is grace, not debt.
- When that covered end is reached, the shell locks, logs the member out, and leaves the PC balance at zero or at any unpaid dust. It does not bill time past the covered end, so a late tick cannot push the balance below zero.
- Logout before that keeps whatever toman and package minutes were not consumed.
- A shared-wallet debt blocks PC login unless the branch explicitly allows package minutes. A separate console wallet does not.
- The demo rate is **60,000 toman/hour** (1,000 toman per minute). It is not a live cafe price. The label in code is `DemoStand`.

The countdown uses a flat hourly rate captured at login, plus package minutes at 1:1. It does not preview the next time band. While the process stays up, settlement still goes through the billing engine, so the toman figure matches `CustomerPlay`. A shell restart rebuilds that flat card from the login snapshot stored in SQLite.

## Game list

The launcher shows only games an admin defined: name, cover path, executable or launcher URI (`steam://rungameid/…`, `com.epicgames.launcher:…`, `battlenet:…`), arguments, working directory, category, optional minimum age, optional station-group restriction, enabled flag, and extra process names (anti-cheat helpers).

The list lives on the server. The shell caches it in the local SQLite file at login and uses the cache offline. Disabled games, games for another station group, and age-restricted games (hidden when the member's age is unknown or too low) are not shown and cannot be launched. A catalog entry whose executable is `cmd.exe` or another blocked tool is rejected.

The guard kills escape tools only in the kiosk user's session. An admin session, session 0, and SYSTEM are not touched. Browsers (Chrome, Edge, Firefox, Opera, Internet Explorer, Brave, and a renamed copy recognized by its original file name) are killed and written to `guard.log`. Listed games stay allowed. `explorer.exe` in the kiosk session is closed.

Steam, Epic, and Battle.net no longer support Windows 7 or 8.x. On those versions the shell refuses `steam:`, `com.epicgames.launcher:`, and `battlenet:` URIs and tells the member the game needs Windows 10 or 11. A normal `.exe` is still launched. Valorant, Vanguard, FACEIT, and Easy Anti-Cheat are not expected to install on Windows 7 or 8.1.

## Guard, pipes, offline log

`GameClub.Kiosk.Guard` is a `ServiceBase` service (`GameClubKioskGuard`, LocalSystem).

- If `GameClub.Kiosk` is not running, it starts the path in `%ProgramData%\GameClub\shell.path`. Five restarts inside five minutes back off.
- A heartbeat older than 15 seconds counts as a hung shell and is restarted the same way. A shell that has just started and has not heartbeated yet is left alone.
- It kills the deny-list processes above. It reads the cached catalog so enabled games and their helpers are recognized.
- Named pipe `GameClub.Kiosk`, length-prefixed UTF-8 JSON. The shell sends `heartbeat` and `admin-escape`.
- Ctrl+Shift+F12 inside the shell asks for the admin password (PBKDF2-SHA256, 100,000 iterations, hash file `%ProgramData%\GameClub\admin-gate.json`). Five failures lock the prompt for one minute. Success pauses enforcement for 10 minutes and starts `explorer.exe`. There is **no global keyboard hook**: anti-cheat products often flag those. From inside a game, Alt+Tab back to the shell, then the hotkey.
- SQLite file `%ProgramData%\GameClub\kiosk.db` stores the open session, the catalog cache, and the event log. Play time is accumulated from a monotonic counter and a checkpoint, not from the PC wall clock. Event timestamps are Tehran `+03:30`, rebuilt from the UTC instant, so a Windows 7 box that still applies the old Iran daylight-saving offset does not change the log.
- On startup the shell resumes from the last checkpoint and does not bill the time the PC was off. If the gap is longer than the absence timeout (default 10 minutes, configurable), the session closes at that checkpoint and the member must log in again.
- Logout reports a session id. The server prices with its own clock, its own rate card, and its own heartbeats. It ignores the client's hourly rate, start, and end. The same session id is applied once.

The kiosk clock is UTC converted with the fixed Tehran offset `+03:30`. It does not trust the PC's local time zone setting. Instants passed into the billing engine still have to carry that offset.

Trust limit, on purpose: a customer who can edit the SQLite file or the clock can lie about the interval. The guard log is an audit trail, not a tamper-proof clock. The real server should keep its own heartbeats. This spike recomputes money from the reported interval with the billing engine so a forged "remaining balance" field is not what gets saved.

## Server client

`ICafeServer` is the hub the shell needs: `Login`, `Logout`, `GetGameCatalog`. `InMemoryCafeServer` implements it for standalone runs. `ConnectionGate` drops calls when `Online` is false so a session can continue on the cache.

`SignalRCafeServerClient` (in the WPF project) maps that interface onto a future hub. Transport bits match ASP.NET Core `HttpTransportType`:

| OS | Transports |
| --- | --- |
| Windows 7 | Server-Sent Events and long polling only. `ClientWebSocket` is not supported and throws `PlatformNotSupportedException`. |
| Windows 8.1, 10, 11 | WebSockets, then Server-Sent Events, then long polling. |

Point the shell at a hub with a `kiosk.json` beside the exe: `{ "HubUrl": "https://cafe-server:5000/hubs/kiosk" }`. An empty or missing file uses the demo server.

Demo logins, standalone only: `reza` / `reza-demo` (20 years, 200,000 toman) and `neda` / `neda-demo` (15 years, 2,000 toman, so the grace minute shows quickly and the 16+ game is hidden).

## Install and uninstall

Run an elevated `scripts\install-kiosk.cmd` (or the PowerShell 2.0 `install-kiosk.ps1`) on the gaming PC:

```bat
install-kiosk.cmd /shell "C:\Program Files\GameClub\GameClub.Kiosk.exe" /guard "C:\Program Files\GameClub\GameClub.Kiosk.Guard.exe" /user GameClubKiosk /userpassfile user.txt /adminpassfile admin.txt
```

`user.txt` and `admin.txt` are one line each. The user password is passed to `net user` (visible to other admins for that moment). The admin password is hashed by `GameClub.Kiosk.Guard.exe hash-admin-file` and the text file should be deleted afterwards. Do not put a real password in the repo.

The script:

1. Refuses original Windows 8. Requires the .NET Framework 4.8 release key (`Release >= 528040`).
2. On Windows 7, reminds you about KB4474419 (SHA-2 code signing) and KB3140245 (TLS 1.2). A signed installer will not validate on Windows 7 without the SHA-2 update. .NET Framework 4.8's own installer has the same requirement. The script then sets `SchUseStrongCrypto`, `SystemDefaultTlsVersions`, WinHTTP `DefaultSecureProtocols` (`0xA00`), and the SChannel TLS 1.2 client key. Uninstall does **not** remove those. They are OS/runtime settings, not kiosk-only, and turning TLS 1.2 back off would break other programs.
3. Creates a standard user (not an administrator) and loads that user's `NTUSER.DAT`.
4. Sets **that user's** `Winlogon\Shell` to the kiosk exe. It does not write the machine Winlogon shell, so an admin logon still gets Explorer.
5. Writes the policy values below and installs the guard service.
6. Writes `%ProgramData%\GameClub\shell.path`.

`uninstall-kiosk.cmd /user GameClubKiosk` stops the service, sets the user's shell back to `explorer.exe`, and deletes the policy values. `/removeuser` also deletes the account. The kiosk user must be logged off so the hive can be loaded.

### Policies, and where Windows versions differ

Applied with `reg.exe`, not `gpedit`. Home editions do not ship `gpedit.msc`; the same registry values are what Group Policy writes, and Home reads them.

| Policy | Value | Windows 7 / 8.1 | Windows 10 / 11 |
| --- | --- | --- | --- |
| Task Manager | per-user `DisableTaskMgr` | Blocks the Task Manager UI | Same. The guard also kills `taskmgr.exe` if it starts. |
| Ctrl+Alt+Del | `DisableLockWorkstation`, `DisableChangePassword`, machine `HideFastUserSwitching` | The secure attention sequence itself cannot be turned off on any of these versions. The options on that screen are removed. | Same. The Windows 11 security screen looks different; the options are still policy-controlled. |
| Windows key | per-user `NoWinKeys` | Win+R, Win+E, Win+X hotkeys off. Ctrl+Esc can still open a menu on some builds if Explorer is alive. Explorer is not the shell. | Same policy. Win11 still honors `NoWinKeys`. |
| Run dialog | per-user `NoRun` | Win+R and the Run dialog | Same |
| Control Panel | per-user `NoControlPanel` | Enough. There is no Settings app. | Classic Control Panel is blocked. The Settings app is a separate hole. `SettingsPageVisibility=hide:allpages` is written everywhere and only means something on Windows 10 1703+. The guard also kills `SystemSettings.exe`, which does not exist on 7/8.1. |
| USB autorun | machine `NoDriveTypeAutoRun=255`, `NoAutorun=1` | No AutoPlay launch | Same. A file dialog can still open the drive. That is a known gap, not something autorun policy can close. |

`SettingsPageVisibility` is ignored on Windows 7 and 8.1. Writing it there is harmless.

## What the Linux tests cover

`dotnet test` on this machine:

- Billing engine: **50** tests (unchanged behavior; the library now also builds `netstandard2.0`).
- Kiosk core: **35** tests.

Covered without Windows:

- Money and time text, including the grace minute and a mid-minute countdown.
- Lock at the covered end, including a late tick, with the PC balance at zero and not negative.
- Logout keeping the remainder. Grace disabled leaving the unpaid dust.
- Package minutes. Shared-wallet debt blocked. Separate console debt not blocking PC. Free rate not locking.
- Catalog filter: enabled, age, unknown age, station group. Launch of an unlisted, disabled, or wrong-group game refused. `cmd.exe` refused. Steam/Epic/Battle.net URIs refused on Windows 7 and 8.1 and allowed on Windows 10+.
- Process decisions: `taskmgr`, `cmd`, `powershell`, `pwsh`, `regedit`, `utilman`, `sethc`, `SystemSettings` killed; unelevated Explorer killed; elevated Explorer kept; Valorant, Vanguard helpers, Easy Anti-Cheat, Steam, and the shell itself kept; a substring like `task` not killed.
- Guard restart, heartbeat timeout, admin-escape stand-down, restart backoff.
- SQLite catalog cache, session resume after a new process, offline ticks that do not touch the server, reconciliation that ignores a client balance and resettles a top-up, idempotent logout.
- Pipe frame decode of a partial buffer.
- Windows version gates, including "Windows 8 must become 8.1", and the SignalR transport mask.
- PBKDF2 admin gate, including the one-minute lockout.
- The install scripts contain the policy values and the per-user shell, and do not mention Shell Launcher or Assigned Access.

## What you have to try on a real PC

WPF, the service, the registry, and games were not run here. Use a spare PC, not the only admin machine. Keep an admin account that does **not** get the kiosk shell. Log the kiosk user off before install and uninstall.

Standalone demo is enough for the shell tests. For Steam, Epic, Battle.net, Valorant/Vanguard, FACEIT, and Easy Anti-Cheat, use Windows 10 or 11. Those launchers do not support Windows 7 or 8.1; on those two, confirm the shell shows the "needs Windows 10" message for a `steam://` game and that a local exe still starts.

| # | Do this | Expected |
| --- | --- | --- |
| 1 | Log in as `reza` / `reza-demo` | Lock screen is Persian, right to left. After login: toman balance, remaining time, and only enabled games for this PC group. `vip-only` and the disabled game are absent. |
| 2 | Log in as `neda` / `neda-demo` | Valorant is hidden (age 15, minimum 16). Balance is small. Within about two minutes the screen shows the grace minute, then locks with balance 0, not negative. |
| 3 | `reza` plays a few minutes and presses خروج | Lock screen returns. Log in again: the toman figure is lower by the minutes played, not zeroed. |
| 4 | Alt+Tab between the shell and a running local game | The game and the shell both appear. The shell is minimized while the game runs so it does not cover it. There is no Explorer desktop to land on. |
| 5 | Windows key, Win+R, Win+E, Win+X | Nothing useful. `NoWinKeys` and `NoRun`. If a stray Explorer window appears, the guard closes it within about a second. |
| 6 | Ctrl+Shift+Esc | Task Manager does not stay open. Policy hides it, and the guard kills `taskmgr.exe` if it starts. |
| 7 | Ctrl+Alt+Del | The security screen still appears. This cannot be removed on 7, 8.1, 10, or 11. Lock, change password, and switch user are removed. Task Manager on that screen does not stay open. |
| 8 | Control Panel, and on Windows 10/11 the Settings app | Control Panel does not open (`NoControlPanel`). On 10/11, Settings does not stay open (`SystemSettings.exe` killed, `SettingsPageVisibility` hides pages). On 7 and 8.1 there is no Settings app; `NoControlPanel` is the whole story. |
| 9 | Insert a USB stick with an `autorun.inf` | Nothing launches. Opening the drive from a game's save dialog still works. That browse is a known gap. |
| 10 | From a game's save/open dialog, type `cmd` or `powershell` in the address bar, or start `regedit` | The window may flash. The guard kills `cmd`, `powershell`, `pwsh`, and `regedit`. |
| 11 | Steam overlay (Shift+Tab), Steam's built-in browser, Epic overlay, Battle.net friends list | On Windows 10/11 they keep running. They are not on the deny list. They can browse the web and download files. That is a known gap until a later allow-list of window classes. On Windows 7/8.1 these clients are not installed; expect the shell's "needs Windows 10" message instead. |
| 12 | Valorant (Vanguard), a FACEIT client, an EAC game | On Windows 10/11 the game starts. The guard does not inject, does not hook the keyboard globally, and does not kill `vgc`, `vgtray`, or `EasyAntiCheat`. If Vanguard refuses to start, capture the exact error before changing the guard. On Windows 7/8.1 these titles are out of scope. |
| 13 | Kill `GameClub.Kiosk` from an admin logon (or `taskkill` from an admin) | The guard starts it again within a few seconds, unless it has already restarted five times in five minutes. The balance continues from the SQLite snapshot, it does not reset to the login amount. |
| 14 | Pull the network cable mid-session, play two minutes, reconnect | The timer keeps moving. After reconnect, logout posts one interval. The server balance matches the shell. Doing it twice does not charge twice. |
| 15 | Ctrl+Shift+F12 on the shell, wrong password five times, then the right one | Wrong passwords fail. The sixth try inside a minute fails even if it is correct. After a minute the right password pauses the guard for 10 minutes and opens Explorer. Relock by restarting the service or waiting. |
| 16 | Shift five times (sticky keys), and the ease-of-access button if it appears | A sticky-keys prompt may show. `sethc.exe` and `utilman.exe` are killed if they are started as a way out. This does not replace those binaries. |
| 17 | Boot Safe Mode, or hold Shift while clicking Restart | Expected weakness. Safe Mode does not use this shell the same way, and the guard service may not start. Do not "fix" this by disabling Safe Mode from the script. Use a firmware password and BitLocker on the drive, as a later hardening step. Write down what you actually see on each Windows version. |
| 18 | A game crash dialog (`WerFault`) | It may appear. The guard does not kill it, so a crash cannot hang the game by leaving a hidden fault window. It must not offer a path into `cmd`. If it does, that is a fail to record. |
| 19 | Installer on Windows 8.0 (not 8.1) | Script stops and says to upgrade to 8.1. |
| 20 | Installer on Windows 7 SP1 without KB4474419 | .NET Framework 4.8 or a signed setup will fail trust. After KB4474419 and KB3140245, the script's TLS keys let .NET 4.8 speak TLS 1.2 to the future server. |
| 21 | Same installer on Home, with no `gpedit.msc` | Policies still apply. Check Task Manager and Win+R under the kiosk user. |
| 22 | Uninstall, then log in as the kiosk user | Explorer is back. An administrator logon was never switched to the kiosk shell. |

Record the Windows edition, build number, and whether the game's anti-cheat printed a specific error. Do not install a kernel driver or a global hook to "fix" a failed anti-cheat test without reading that error first.
