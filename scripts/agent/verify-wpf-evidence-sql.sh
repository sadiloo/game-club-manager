#!/usr/bin/env bash
# Verifies ci-wpf-stability-failure-evidence.ps1 PostgreSQL snippets against a live DB.
set -euo pipefail
export PGPASSWORD="${PGPASSWORD:-postgres}"
HOST="${PGHOST:-localhost}"
PORT="${PGPORT:-5432}"
DB="${PGDATABASE:-gameclub_test}"
USER="${PGUSER:-postgres}"
LOGIN="${VERIFY_LOGIN:-fundtest0001}"
LOGIN_NORM="$(printf '%s' "$LOGIN" | tr '[:upper:]' '[:lower:]')"

if ! command -v psql >/dev/null 2>&1; then
  echo "ERROR: psql not installed"
  exit 1
fi

run() {
  echo "--- $1"
  shift
  psql -h "$HOST" -p "$PORT" -U "$USER" -d "$DB" -v ON_ERROR_STOP=1 "$@"
}

run 'stations all columns' -c 'SELECT * FROM stations ORDER BY "Id";'

run 'last 20 sessions with station join' -c "
SELECT s.*, st.\"Id\" AS \"StationJoin_Id\", st.\"Name\" AS \"StationJoin_Name\", st.\"GroupId\" AS \"StationJoin_GroupId\",
       st.\"DefaultPlayMode\" AS \"StationJoin_DefaultPlayMode\", st.\"DefaultPlayerCount\" AS \"StationJoin_DefaultPlayerCount\",
       st.\"IsActive\" AS \"StationJoin_IsActive\", st.\"OperationalState\" AS \"StationJoin_OperationalState\"
FROM sessions s
LEFT JOIN stations st ON st.\"Id\" = s.\"StationId\"
ORDER BY s.\"StartedAtUtc\" DESC NULLS LAST
LIMIT 20;"

run 'customer_credentials for login' -c "
SELECT * FROM customer_credentials
WHERE \"LoginNameNormalized\" = '$LOGIN_NORM' OR \"LoginNameDisplay\" = '$LOGIN';"

run 'customer_members wallet for login' -c "
SELECT m.*
FROM customer_members m
INNER JOIN customer_credentials c ON c.\"CustomerId\" = m.\"CustomerId\"
WHERE c.\"LoginNameNormalized\" = '$LOGIN_NORM' OR c.\"LoginNameDisplay\" = '$LOGIN';"

run 'sessions for funded login' -c "
SELECT s.*, st.\"OperationalState\" AS \"StationOperationalState\"
FROM sessions s
LEFT JOIN stations st ON st.\"Id\" = s.\"StationId\"
WHERE s.\"CustomerId\" = (
  SELECT \"CustomerId\" FROM customer_credentials
  WHERE \"LoginNameNormalized\" = '$LOGIN_NORM' OR \"LoginNameDisplay\" = '$LOGIN'
  LIMIT 1)
ORDER BY s.\"StartedAtUtc\" DESC NULLS LAST
LIMIT 20;"

run 'PC-WPF stations recent' -c "SELECT * FROM stations WHERE \"Id\" LIKE 'PC-WPF-%' ORDER BY \"Id\" DESC LIMIT 10;"

echo "verify-wpf-evidence-sql: OK"