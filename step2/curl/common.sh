# BASE_URL (default http://127.0.0.1:5199) and API_KEY (default dev-key) for a running PR #4 server.
BASE_URL="${BASE_URL:-http://127.0.0.1:5199}"; API_KEY="${API_KEY:-dev-key}"
api() { local m=$1 u=$2 d=${3:-}; if [ -n "$d" ]; then curl -s -o /tmp/qa_body -w '%{http_code}' -X "$m" -H "X-Api-Key: $API_KEY" -H 'Content-Type: application/json' -d "$d" "$BASE_URL$u"; else curl -s -o /tmp/qa_body -w '%{http_code}' -X "$m" -H "X-Api-Key: $API_KEY" "$BASE_URL$u"; fi; }
