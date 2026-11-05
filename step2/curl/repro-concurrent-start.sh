#!/usr/bin/env bash
# D-S1: 8 parallel starts on one station. Expect exactly one 201 and one open session.
. "$(dirname "$0")/common.sh"; S="QA-C-$RANDOM"
api POST /api/stations "{\"id\":\"$S\",\"name\":\"c\",\"groupId\":\"ps5\"}" >/dev/null
for i in $(seq 8); do curl -s -o /dev/null -w '%{http_code}\n' -X POST -H "X-Api-Key: $API_KEY" -H 'Content-Type: application/json' -d "{\"stationId\":\"$S\",\"playerCount\":1}" "$BASE_URL/api/sessions" & done | sort | uniq -c; wait
