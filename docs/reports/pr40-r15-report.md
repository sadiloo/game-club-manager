# PR #40 R15 report (staging `cursor/pr40-r15-staging`)

Parent: `07e524f24cf33bb820197dc6c8e98a6b2ef8713d`

## Summary

Guard OFF: allow rules on **ActiveStore** (no `-PolicyStore`) **before** profile `Block`; rollback removes rules + profiles; C# re-read + `InternetFirewallOffVerification` via `InternetFirewallOffApplyReadback`; `applied` only when classifier returns null. Verified `internet-state.json` only from `InternetGuardStateStore.SaveVerified` after signed apply in `InternetPipeServer` (not applier). L17 prints `L17_OFF_APPLY_DIAG` after apply. `Invoke-CiFirewallRestore` before guard-wpf retained (CI evidence below).

## Root cause (a) — proved from R14 CI job 135509470398

1. `L17_GUARD_OFF_PROBE apply_result=applied blocked=False guard_rule_count=2 state_present=False` (log ~11:07:20Z).
2. Same job **before** `dotnet-test-net48-guard-wpf`: `CI_FIREWALL active_profile … out=NotConfigured` (all three), `localhost_profile … out=NotConfigured`, `guard_rule_count … count=2`, `CI_EGRESS … public=1.1.1.1:443 blocked=False`, `checkpoint=127.0.0.1:5225 reachable=True`.

**Interpretation:** R14 `InternetFirewallApplier` (`07e524f`) created **Checkpoint Internet Off** rules in **localhost PolicyStore only**, then set Block only on localhost profiles. ActiveStore outbound stayed `NotConfigured`, so the script’s localhost-only verify could succeed while egress remained open (2 rules visible to localhost rule query, no effective block on ActiveStore). `state_present=False` because L17 calls `InternetFirewallApplier.Apply` directly — `InternetGuardStateStore.SaveVerified` runs only on `InternetPipeServer` signed `apply`, not on the probe’s raw applier call.

**R15 proof lines:** after apply, CI will emit `L17_OFF_APPLY_DIAG active_profiles_out=… localhost_profiles_out=… active_group_rule_count=… localhost_group_rule_count=…`.

## Root cause (c) — host timer regressions (same R14 change, CI evidence)

**Change:** `InternetFirewallApplier.cs` in `dba1e26..07e524f` — rule creation moved before profile block; rules pinned to `-PolicyStore localhost`.

**Evidence (job 135509470398):** After server integration (incl. L17 probe), `before-dotnet-test-net48-guard-wpf` shows `guard_rules=2`, `public … blocked=False`, profiles `out=NotConfigured` on ActiveStore — leftover localhost rules without block. Host WPF tests (`KioskHostStatusTimerTests`) run in that phase; shared runner egress/checkpoint reachability matches R12 environmental coupling (ctor `RefreshAsync` vs mock transport). **Not** masking product bugs: restore clears orphan rules before guard-wpf; applier fix prevents new localhost-only partial OFF.

## Files

- `src/GameClub.Kiosk.Guard/Internet/InternetFirewallApplier.cs`, `InternetFirewallOffApplyReadback.cs`, `InternetPipeServer.cs`, `InternetFirewallOffDiagnostics.cs`
- `src/GameClub.Kiosk.Core/Guard/InternetFirewallOffVerification.cs`, `InternetFirewallOffReadbackModel.cs`
- `tests/GameClub.Kiosk.Core.Tests/PcInternetFirewallOffVerificationTests.cs`
- `tests/.../WindowsPcInternetApplyFailureLiveTests.cs` (L17 diag line only)
- `scripts/windows/ci-integration.ps1` (restore before guard-wpf)

## Staging SHA

_(after push — full sha from `git rev-parse HEAD`)_

## Open question (L17 state assertion)

Probe still calls applier only; `state_present=True` requires signed `InternetPipeServer` apply (same path as production). Applier intentionally uses `requireVerifiedStateFile: false`. If L17 must pass applier-only, need PM rule; otherwise wire probe through pipe with signed command without changing assertion text.