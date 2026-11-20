#!/usr/bin/env bash
# Verifies ci-wpf-stability-failure-evidence.ps1 PostgreSQL snippets against a live DB.
set -euo pipefail
CONN="${GAMECLUB_TEST_DATABASE:-Host=localhost;Port=5432;Database=gameclub_test;Username=postgres;Password=postgres}"
export PGPASSWORD="${PGPASSWORD:-postgres}"
HOST="${PGHOST:-localhost}"
PORT="${PGPORT:-5432}"
DB="${PGDATABASE:-gameclub_test}"
USER="${PGUSER:-postgres}"
if ! command -v psql >/dev/null 2>&1; then
  echo "SKIP: psql not installed (Windows CI has PostgreSQL 16 psql)"
  exit 0
fi
psql -h "$HOST" -p "$PORT" -U "$USER" -d "$DB" -v ON_ERROR_STOP=1 -c 'SELECT * FROM stations ORDER BY "Id" LIMIT 1;'
psql -h "$HOST" -p "$PORT" -U "$USER" -d "$DB" -v ON_ERROR_STOP=1 -c '
SELECT s.*, st."Id" AS "StationJoin_Id"
FROM sessions s
LEFT JOIN stations st ON st."Id" = s."StationId"
ORDER BY s."StartedAtUtc" DESC NULLS LAST
LIMIT 1;'
echo "verify-wpf-evidence-sql: OK"