#!/usr/bin/env bash
# QA step-2 suite for PR #4. Needs PostgreSQL; QA_PG = base connection string without Database.
# Usage: QA_PG="Host=127.0.0.1;Port=55432;Username=qa;Password=qa" ./run.sh [Pr4Root]
set -euo pipefail
cd "$(dirname "$0")/QaServerTests"
ROOT="${1:-$(cd "$(dirname "$0")/../.." && pwd)}"
export PR4_ROOT="$ROOT"
export QA_PG="${QA_PG:-Host=127.0.0.1;Port=55432;Username=qa;Password=qa}"
OUT="${OUT:-/workspace/qa/regress-pr4-b5de45f/results}"
nice -n 10 dotnet test -m:1 -p:Pr4Root="$ROOT" --logger "trx;LogFileName=$OUT/qa-step2.trx" -- xUnit.ParallelizeTestCollections=false 2>&1 | tee "$OUT/qa-step2.log"
