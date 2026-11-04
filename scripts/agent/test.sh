#!/usr/bin/env bash
# Quiet tests for agents.
# VSTest (xunit + Microsoft.NET.Test.Sdk): --logger "console;verbosity=minimal".
# Not Microsoft.Testing.Platform.
# `all` matches CI: dotnet test GameClub.slnx -c Release -f net10.0 on Linux.
# A single project stays on the SDK default configuration (Debug).
# Linux adds -f net10.0 unless the caller passes -f or --framework.
# Usage: test.sh <tests.csproj|all> [dotnet test args, e.g. --filter "..."] [--verbose]
set -uo pipefail
target="${1:-all}"; [ $# -gt 0 ] && shift
verbose=0; args=()
for a in "$@"; do
  if [ "$a" = "--verbose" ]; then verbose=1; else args+=("$a"); fi
done
is_windows_kiosk() {
  case "$1" in
    *GameClub.Kiosk.Core*) return 1 ;;
  esac
  case "$1" in
    *src/GameClub.Kiosk/*|*src/GameClub.Kiosk.Guard/*|*src/GameClub.Kiosk|*src/GameClub.Kiosk.Guard|*/kiosk/*|kiosk/*)
      return 0 ;;
  esac
  return 1
}
fw=()
case " ${args[*]-} " in
  *" -f "*|*" --framework "*) ;;
  *) [ "$(uname -s)" = Linux ] && fw=(-f net10.0) ;;
esac
cfg=()
if [ "$target" = "all" ]; then
  case " ${args[*]-} " in
    *" -c "*|*" --configuration "*) ;;
    *) cfg=(-c Release) ;;
  esac
  if [ -f GameClub.slnx ]; then
    projs=(GameClub.slnx)
  else
    mapfile -t projs < <(find . -name '*Tests.csproj' \
      -not -path './src/GameClub.Kiosk/*' \
      -not -path './src/GameClub.Kiosk.Guard/*' \
      -not -path './kiosk/*' \
      | sort)
  fi
else
  if is_windows_kiosk "$target"; then
    echo "refusing: $target is the net48 kiosk or guard and is not tested on Linux (compile with scripts/agent/build.sh)" >&2
    exit 1
  fi
  projs=("$target")
fi
log="$(mktemp /tmp/agent-test-XXXX.log)"
rc=0
for p in "${projs[@]}"; do
  if [ "$verbose" = 1 ]; then
    # shellcheck disable=SC2086
    dotnet test "$p" ${cfg[@]+"${cfg[@]}"} ${fw[@]+"${fw[@]}"} ${args[@]+"${args[@]}"} || rc=$?
  else
    echo "== $p" >>"$log"
    # shellcheck disable=SC2086
    dotnet test "$p" ${cfg[@]+"${cfg[@]}"} ${fw[@]+"${fw[@]}"} --nologo -v q \
      --logger "console;verbosity=minimal" \
      ${args[@]+"${args[@]}"} >>"$log" 2>&1 || rc=$?
  fi
done
if [ "$verbose" = 0 ]; then
  if [ "$rc" = 0 ]; then
    grep -E "^== |Passed!|Total tests" "$log" | tail -n 20 || true
  else
    grep -E "^== |Passed!|Failed!|Test Run Aborted" "$log" || true
    grep -E -A12 ": error |Failed |Assert|Exception|Test Run Aborted" "$log" | head -n 150 || true
  fi
  echo "full log: $log"
fi
exit "$rc"
