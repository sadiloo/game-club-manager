# PR #40 R13 report (staging cursor/pr40-r13-staging)

Parent: 87d623ba5f711332aad0d9469021d29609628faa

## Summary

Harness and diagnostics for R12 reds: shared PC-band arg parser, preserve lease signing keys on live DB reset, LEASE_BUILD log capture from relaunched TestHost, reconnect signing evidence, host-timer failure dumps, honest CI_EGRESS labels, ubuntu G2 count line, funded 80k day-band lease test.

## Expected CI log lines (by brief item)

### 1) G2 band guard

- `live_server_start pc_band=explicit start=HH:mm end=HH:mm` (ubuntu G2 launch)
- `G2_HARNESS_BAND ...` (existing G2 oracle line)

### 2) Signing key + reconnect

- `LEASE_KEY_FINGERPRINT sha256=<16 hex>` on CI TestHost stdout and each relaunch log
- `LEASE_VERIFY sig_ok=True ... lease_key_k1=... lease_key_k2=... keys_match=True`
- `LEASE_KEY_MATCH` via `keys_match=True` in LEASE_VERIFY line

### 2b) LEASE_BUILD evidence

- `LEASE_BUILD sessionId=...` echoed on test console from relaunch stream
- `LEASE_BUILD phase=dotnet-test-net10-server sourceLog=server-relaunch-<pid>-out.log ...`
- Throw `LEASE_BUILD missing` only after firewall probe when no line in day or relaunch logs

### 3) Host timer

- On failure: assert message includes `statusHistory=`, `consecutiveFailures=`, `transportPhase=`, `hubStartCount=`
- Mutation baseline: `BASELINE_TRX <test>: <message>`

### 4+6) Firewall egress

- `CI_EGRESS label=<phase> guard_rules=<n> state_present=<bool> public=1.1.1.1:443 blocked=...`
- L17 live: `L17_GUARD_OFF_PROBE ... blocked=True guard_rule_count>=1 state_present=True`

### 5) Ubuntu G2 count

- `UBUNTU_G2_TEST_COUNT 1 passed` or job fails with `UBUNTU_G2_TEST_COUNT 0 listed`

### Funded lease test (new)

- Pass: `online_session_wallet_at_80000_day_band_lease_authorizes_hand_computed_minutes`
- If lease minutes disagree with hand-computed 120 min at 80_000/h: product header-mode finding (stop, no pricing fix in harness)

## Product note (no fix)

Changing or truncating kiosk_lease_signing_keys makes kiosks see LeaseExpired (backlog).

## Compile gate (agent box)

0 errors: IntegrationTests net10.0 + net48, GameClub.Server.TestHost, GameClub.Server.Tests (EnableWindowsTargeting=true).