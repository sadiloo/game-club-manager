#!/usr/bin/env bash
# D-S2: client startedAt is honoured. Expect a few-second session to bill ~0; actual = 2 h of PS5.
. "$(dirname "$0")/common.sh"; S="QA-T-$RANDOM"
api POST /api/stations "{\"id\":\"$S\",\"name\":\"t\",\"groupId\":\"ps5\"}" >/dev/null
past=$(date -u -d '-2 hours' +%Y-%m-%dT%H:%M:%SZ)
api POST /api/sessions "{\"stationId\":\"$S\",\"playerCount\":1,\"startedAt\":\"$past\"}" >/dev/null; id=$(python3 -c 'import json;print(json.load(open("/tmp/qa_body"))["id"])')
api POST /api/sessions/$id/stop >/dev/null; echo "stop body: $(cat /tmp/qa_body)  (expected collectibleToman 5000 for a seconds-long session; >=220000 means client time was used)"
