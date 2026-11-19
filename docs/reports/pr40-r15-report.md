# PR #40 R15 report (staging `cursor/pr40-r15-staging`)

Parent: `07e524f24cf33bb820197dc6c8e98a6b2ef8713d`

## Summary

Guard OFF: profiles and rules run in safe order again; ActiveStore **and** localhost must read `Block` before PS emits `applied`; C# persists `internet-state.json` then rejects `applied` if the file is missing. Core `InternetFirewallOffVerification` unit tests (Category `PcInternetFirewall`) wired on ubuntu + windows integration. Firewall restore before guard-wpf. Oracle + core firewall phases named in `ci-integration.ps1`.

## Root cause (a) — false `applied` without block

R14 (`dba1e26..07e524f`) moved **allow rules before** profile `Block` and verified only **localhost** `Get-NetFirewallProfile`. On windows-2022 CI, localhost could report success / rules count 2 while **ActiveStore** `DefaultOutboundAction` stayed `NotConfigured`, so the script tail was `applied`, egress to `1.1.1.1:443` stayed open, and L17 logged `blocked=False` with no verified state file.

## Root cause (c) — host timer regressions

Same R14 reorder: failed or partial OFF left **Checkpoint Internet Off** allow rules on the runner without effective outbound block. Server integration (incl. L17 probe) ran **before** guard-wpf; dirty firewall + timing pushed host status to bare "connected" instead of "no server response". Fix: restore R13 ordering + dual-store verify + `Invoke-CiFirewallRestore` immediately before guard-wpf (not reverting net10 `GameClub.Server` reference — net48 host tests unaffected).

## Files

- `src/GameClub.Kiosk.Guard/Internet/InternetFirewallApplier.cs`
- `src/GameClub.Kiosk.Core/Guard/InternetFirewallOffVerification.cs`
- `tests/GameClub.Kiosk.Core.Tests/PcInternetFirewallOffVerificationTests.cs`
- `scripts/windows/ci-integration.ps1`, `.github/workflows/ci.yml`

## Staging SHA

`4ad0b86dc1e3045e64af6c27ead78fe76be572dd`

## Tests run (agent box)

- Builds: IntegrationTests net48+net10, Server.TestHost, Server.Tests, Core.Tests — 0 errors
- `Category=PcInternetFirewall` (Core): 7 passed
- `PcSpecialBandLiveRateCardOracleTests`: 5 passed
- Full `scripts/agent/test.sh all`: Server/integration failures without local Postgres (expected on Linux agent)