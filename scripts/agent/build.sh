#!/usr/bin/env bash
# Quiet build for agents. Uses the SDK default configuration (Debug).
# CI and `test.sh all` build Release; do not add -c Release here.
# Linux can compile the net48 kiosk and guard. Do not run or test them here.
# Usage: build.sh <project|sln> [dotnet build args...] [--verbose]
set -uo pipefail
target="${1:?usage: build.sh <project|sln> [args] [--verbose]}"; shift
verbose=0; args=()
for a in "$@"; do
  if [ "$a" = "--verbose" ]; then verbose=1; else args+=("$a"); fi
done
if [ "$verbose" = 1 ]; then exec dotnet build "$target" ${args[@]+"${args[@]}"}; fi
log="$(mktemp /tmp/agent-build-XXXX.log)"
# shellcheck disable=SC2086
dotnet build "$target" --nologo -v q -clp:ErrorsOnly ${args[@]+"${args[@]}"} >"$log" 2>&1
rc=$?
if [ "$rc" = 0 ]; then echo "build ok: $target"
else grep -E ": error " "$log" | sort -u | head -n 60; fi
echo "full log: $log"
exit "$rc"
