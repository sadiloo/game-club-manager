#!/usr/bin/env bash
# Quiet build for agents.
# Usage: build.sh <project|sln> [dotnet build args...] [--verbose]
set -uo pipefail
target="${1:?usage: build.sh <project|sln> [args] [--verbose]}"; shift
case "$target" in
  ./src/GameClub.Kiosk/*|src/GameClub.Kiosk/*|./src/GameClub.Kiosk.Guard/*|src/GameClub.Kiosk.Guard/*|./kiosk/*|kiosk/*)
    echo "refusing: $target is Windows-only (kiosk WPF net48 or kiosk guard net48) and is not built on Linux" >&2
    exit 1 ;;
esac
verbose=0; args=()
for a in "$@"; do
  if [ "$a" = "--verbose" ]; then verbose=1; else args+=("$a"); fi
done
if [ "$verbose" = 1 ]; then exec dotnet build "$target" "${args[@]}"; fi
log="$(mktemp /tmp/agent-build-XXXX.log)"
dotnet build "$target" --nologo -v q -clp:ErrorsOnly "${args[@]}" >"$log" 2>&1
rc=$?
if [ "$rc" = 0 ]; then echo "build ok: $target"
else grep -E ": error " "$log" | sort -u | head -n 60; fi
echo "full log: $log"
exit "$rc"
