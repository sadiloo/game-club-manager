#!/usr/bin/env bash
# D-S3: parallel pauses create several open pause rows; resume closes one, rest stay open -> under-billing.
. "$(dirname "$0")/common.sh"; S="QA-P-$RANDOM"
api POST /api/stations "{\"id\":\"$S\",\"name\":\"p\",\"groupId\":\"ps5\"}" >/dev/null
api POST /api/sessions "{\"stationId\":\"$S\",\"playerCount\":1}" >/dev/null; id=$(python3 -c 'import json;print(json.load(open("/tmp/qa_body"))["id"])')
for i in $(seq 6); do curl -s -o /dev/null -w '%{http_code}\n' -X POST -H "X-Api-Key: $API_KEY" "$BASE_URL/api/sessions/$id/pause" & done | sort | uniq -c; wait
echo "session $id — check: psql -c \"select count(*) from pause_intervals where \\\"SessionId\\\"='$id'\" (expected 1)"
