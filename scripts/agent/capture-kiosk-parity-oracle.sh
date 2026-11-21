#!/usr/bin/env bash
# Capture paid kiosk parity bill fingerprint on main 977703de for L-4 oracle tests.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
ORACLE_SHA=977703de90e5aa4deb99ed1ff8bae7f0fdddc095
WT="${TMPDIR:-/tmp}/gcm-oracle-${ORACLE_SHA:0:8}"
rm -rf "$WT"
git -C "$ROOT" worktree add --detach "$WT" "$ORACLE_SHA" >/dev/null
trap 'git -C "$ROOT" worktree remove -f "$WT" 2>/dev/null || true' EXIT

export GAMECLUB_TEST_DATABASE="${GAMECLUB_TEST_DATABASE:-Host=localhost;Port=5432;Database=gameclub_test_oracle_capture;Username=gameclub;Password=gameclub}"
cd "$WT"
scripts/agent/build.sh tests/GameClub.Server.Tests/GameClub.Server.Tests.csproj >/dev/null
OUT="$WT/oracle-bill.txt"
if scripts/agent/test.sh tests/GameClub.Server.Tests/GameClub.Server.Tests.csproj --filter "FullyQualifiedName~emit_main_paid_kiosk_bill_fingerprint" 2>/dev/null | tee "$OUT"; then
  :
fi
echo "See $OUT (worktree $WT). Update KioskPaidSessionMainOracle from emitted fingerprint."