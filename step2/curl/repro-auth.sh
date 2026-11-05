#!/usr/bin/env bash
. "$(dirname "$0")/common.sh"
echo "no key: $(curl -s -o /dev/null -w '%{http_code}' $BASE_URL/api/stations) (401)"
echo "bad key: $(curl -s -o /dev/null -w '%{http_code}' -H 'X-Api-Key: nope' $BASE_URL/api/stations) (401)"
echo "good key: $(api GET /api/stations) (200)"
