# PR #40 r38b staging — round 2 agent report

## Session snapshot after Down (not a main worktree proof)

**Test:** `PcInternetMigrationTests.PcInternetMigration_Down_reverted_session_snapshot_deserialize_bills_without_duplicate_base_rate`

Exercises real Postgres `MigrateAsync` / `IMigrator` (no C# snapshot twin). After Down, open-session snapshot JSON is v1; `RateConfigService.DeserializeSnapshot` (same legacy branch as main `977703d` for v1 JSON) plus `SessionBilling.BuildChargeAsync` bill 80k / 120k with snapshot surcharge 25k; `branch_rounding.PcSpecialBandSurchargeToman` after Down follows **current** `rate_lines` night−day (40k when global night 130k / day 90k).

## Other gate items

- **B3:** `PcInternetMigration_MigratedOpenSession_toggleOff_returns_offline_rate_missing`
- **Snapshots:** migration SQL only; `PcInternetSnapshotSupport` removed
- **B4:** single lease verify on Playing HB ack logging