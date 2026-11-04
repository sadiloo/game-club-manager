#!/usr/bin/env bash
# Quiet tests for agents.
# VSTest (xunit + Microsoft.NET.Test.Sdk): --logger "console;verbosity=minimal".
# Not Microsoft.Testing.Platform; do not switch this to an MTP report flag.
# Usage: test.sh <tests.csproj|all> [dotnet test args, e.g. --filter "..."] [--verbose]
set -uo pipefail
target="${1:-all}"; [ $# -gt 0 ] && shift
verbose=0; args=()
for a in "$@"; do
  if [ "$a" = "--verbose" ]; then verbose=1; else args+=("$a"); fi
done
is_windows_kiosk() {
  case "$1" in
    ./src/GameClub.Kiosk/*|src/GameClub.Kiosk/*|./src/GameClub.Kiosk.Guard/*|src/GameClub.Kiosk.Guard/*|./kiosk/*|kiosk/*)
      return 0 ;;
  esac
  return 1
}
if [ "$target" = "all" ]; then
  mapfile -t projs < <(find . -name '*Tests.csproj' \
    -not -path './src/GameClub.Kiosk/*' \
    -not -path './src/GameClub.Kiosk.Guard/*' \
    -not -path './kiosk/*' \
    | sort)
else
  if is_windows_kiosk "$target"; then
    echo "refusing: $target is Windows-only (kiosk WPF net48 or kiosk guard net48) and is not tested on Linux" >&2
    exit 1
  fi
  projs=("$target")
fi
log="$(mktemp /tmp/agent-test-XXXX.log)"
rc=0
for p in "${projs[@]}"; do
  if [ "$verbose" = 1 ]; then
    dotnet test "$p" "${args[@]}" || rc=$?
  else
    echo "== $p" >>"$log"
    dotnet test "$p" --nologo -v q --logger "console;verbosity=minimal" \
      "${args[@]}" >>"$log" 2>&1 || rc=$?
  fi
done
if [ "$verbose" = 0 ]; then
  if [ "$rc" = 0 ]; then
    grep -E "^== |Passed!|Total tests" "$log" | tail -n 20 || true
  else
    grep -E -A12 ": error |Failed |Assert|Exception" "$log" | head -n 150 || true
  fi
  echo "full log: $log"
fi
exit "$rc"
