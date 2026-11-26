#!/usr/bin/env bash
# Box (Linux + wine) behaviour legs for the Checkpoint Inno installers — see README.md.
# Compiles all four installers (server release/hooks, kiosk release/hooks) with Inno Setup 6.7.1 under wine
# against stub payloads, then runs them silently with a fake powershell.exe and asserts:
#   per-phase exit codes, stop at the first failed phase, upgrade restore WITH a pre-upgrade dump, and
#   F6: upgrade fails before a pre-upgrade dump exists => CheckpointServer is never started (DB unmigrated).
# usage: ISCC_PREFIX=/path/to/wineprefix-with-C:\is\ISCC.exe ./Invoke-WineInstallerLegs.sh [workdir]
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../.." && pwd)"
: "${ISCC_PREFIX:?set ISCC_PREFIX to a wine prefix that has Inno Setup 6.7.1 at C:\\is\\ISCC.exe}"
WORK="${1:-$(mktemp -d /tmp/checkpoint-wine-legs.XXXXXX)}"; mkdir -p "$WORK"
export WINEDEBUG=-all
FAILS=0; PASSES=0
pass() { echo "PASS $*"; PASSES=$((PASSES+1)); }
fail() { echo "FAIL $*"; FAILS=$((FAILS+1)); }
winp() { WINEPREFIX="$ISCC_PREFIX" winepath -w "$1" 2>/dev/null; }

echo "== build fakes"
i686-w64-mingw32-gcc -O2 -o "$WORK/fakeps32.exe" "$HERE/fakeps.c"
x86_64-w64-mingw32-gcc -O2 -o "$WORK/fakeps64.exe" "$HERE/fakeps.c"
x86_64-w64-mingw32-gcc -O2 -o "$WORK/stubserver.exe" "$HERE/stubserver.c"

echo "== stage stub payloads (same tool sync as scripts/windows/ci-installer.ps1 Sync-InstallerStagedTools)"
stage() { # <mode Release|Hooks> <dir>
  local mode=$1 d=$2; rm -rf "$d"; mkdir -p "$d/server/tools" "$d/guard/tools" "$d/kiosk" "$d/pg"
  cp "$WORK/stubserver.exe" "$d/server/GameClub.Server.exe"; cp "$WORK/stubserver.exe" "$d/guard/GameClub.Guard.exe"
  cp "$WORK/stubserver.exe" "$d/kiosk/GameClub.Kiosk.exe"; echo stub > "$d/pg/x.txt"; cp "$WORK/stubserver.exe" "$d/wv2.exe"
  find "$ROOT/installer/tools" -maxdepth 1 -type f \( -name '*.ps1' -o -name '*.psm1' \) -exec cp {} "$d/server/tools/" \;
  cp "$ROOT/installer/ci/checkpoint-setup-exit-codes.ps1" "$d/server/tools/"
  if [ "$mode" = Release ]; then pwsh -NoProfile -File "$ROOT/installer/ci/Strip-ReleaseInstallerPayload.ps1" -ToolsDir "$d/server/tools" >/dev/null; fi
  for n in kiosk-postinstall.ps1 CheckpointInstall.Common.psm1 checkpoint-setup-exit-codes.ps1; do cp "$d/server/tools/$n" "$d/guard/tools/"; done
}
stage Release "$WORK/stage-release"; stage Hooks "$WORK/stage-hooks"

echo "== compile 4 installers (ISCC 6.7.1)"
mkdir -p "$WORK/out"
compile() { # <iss dir> <iss> <hooks 0|1> <stage dir> <label>
  local dir=$1 iss=$2 h=$3 st=$4 label=$5 stw; stw="$(winp "$st")"
  ( cd "$ROOT/installer/$dir" && WINEPREFIX="$ISCC_PREFIX" timeout 900 wine 'C:\is\ISCC.exe' /Q /DAppVersion=9.9.9 /DCheckpointInternalTestHooks=$h \
      "/O$(winp "$WORK/out")" "/DStagingServerDir=$stw\\server" "/DStagingPgDir=$stw\\pg" "/DStagingWebView2=$stw\\wv2.exe" \
      "/DStagingKioskDir=$stw\\kiosk" "/DStagingGuardDir=$stw\\guard" "$iss" ) > "$WORK/iscc-$label.txt" 2>&1 \
    && pass "compile $label" || { fail "compile $label: $(grep -m1 -iE 'error|unknown' "$WORK/iscc-$label.txt")"; }
}
compile server CheckpointServer.iss 0 "$WORK/stage-release" server-release
compile server CheckpointServer.iss 1 "$WORK/stage-hooks" server-hooks
compile kiosk CheckpointKiosk.iss 0 "$WORK/stage-release" kiosk-release
compile kiosk CheckpointKiosk.iss 1 "$WORK/stage-hooks" kiosk-hooks
SREL="$WORK/out/CheckpointServerSetup-9.9.9.exe"; SHK="$WORK/out/CheckpointServerSetupHooks-9.9.9.exe"
KREL="$WORK/out/CheckpointKioskSetup-9.9.9.exe"; KHK="$WORK/out/CheckpointKioskSetupHooks-9.9.9.exe"
for f in "$SREL" "$SHK" "$KREL" "$KHK"; do [ -f "$f" ] || { echo "missing $f"; echo "SUMMARY pass=$PASSES fail=$((FAILS+1))"; exit 1; }; done

echo "== run prefix (copy of ISCC_PREFIX + fake powershell.exe)"
BASE="$WORK/wp-base"; RUN="$WORK/wp-run"; rm -rf "$BASE"; cp -a "$ISCC_PREFIX" "$BASE"
cp "$WORK/fakeps64.exe" "$BASE/drive_c/windows/system32/WindowsPowerShell/v1.0/powershell.exe"
cp "$WORK/fakeps32.exe" "$BASE/drive_c/windows/syswow64/WindowsPowerShell/v1.0/powershell.exe"
printf '{"manager1User":"m1","manager1Pin":"5678","manager2User":"m2","manager2Pin":"8765","operatorUser":"op","operatorPin":"4321"}' > "$WORK/acc.json"
echo '[]' > "$WORK/pins.json"
ACC="/ACCOUNTS=$(winp "$WORK/acc.json")"; PINS="/DEFAULTPINS=$(winp "$WORK/pins.json")"

fresh_prefix() { WINEPREFIX="$RUN" wineserver -k 2>/dev/null || true; sleep 1; rm -rf "$RUN"; cp -a "$BASE" "$RUN"; }
# run <label> <exe> <failspec "phase code;phase code"> [setup args...]  -> sets RC, CALLS, LOG
run() {
  local label=$1 exe=$2 fs=$3; shift 3
  LOG="$WORK/inno-$label.log"; rm -f "$LOG" "$RUN/drive_c/fakeps-calls.log"
  echo "$fs" | tr ';' '\n' > "$RUN/drive_c/fakeps-fail.txt"
  set +e; WINEPREFIX="$RUN" timeout 600 wine "$exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART "/LOG=$(winp "$LOG")" "$@" >/dev/null 2>&1; RC=$?; set -e
  WINEPREFIX="$RUN" wineserver -w 2>/dev/null || true
  CALLS="$(tr -d '\r' < "$RUN/drive_c/fakeps-calls.log" 2>/dev/null | tr '\n' ',' || true)"
  echo "   run $label exit=$RC calls=[$CALLS]"
}
# Make the prefix look like an installed server so the next Setup is an upgrade (IsFirstInstall=false).
seed_upgrade() {
  fresh_prefix; run seed "$SREL" "" "$ACC"
  mkdir -p "$RUN/drive_c/ProgramData/Checkpoint/config"
  echo '{"ConnectionStrings":{"Default":"Host=127.0.0.1;Port=5433;Database=checkpoint;Username=postgres;Password=x"},"OperatorSessionSecret":"s","GAMECLUB_API_KEY":"k"}' \
    > "$RUN/drive_c/ProgramData/Checkpoint/config/server.json"
  cp "$WORK/stubserver.exe" "$RUN/drive_c/Program Files/Checkpoint/Server/GameClub.Server.exe"
  WINEPREFIX="$RUN" wine sc create CheckpointServer binPath= 'C:\Program Files\Checkpoint\Server\GameClub.Server.exe' >/dev/null 2>&1 || true
  WINEPREFIX="$RUN" wine sc create CheckpointPostgres binPath= 'C:\windows\system32\cmd.exe' >/dev/null 2>&1 || true
  WINEPREFIX="$RUN" wineserver -w 2>/dev/null || true
  rm -f "$RUN/drive_c/checkpoint-server-exec.log"
}
server_started() { grep -v -- '--check-default-pins' "$RUN/drive_c/checkpoint-server-exec.log" 2>/dev/null | grep -q . ; }
server_not_started() { ! server_started; }
expect() { # <label> <cond-description> <command...>
  local label=$1 what=$2; shift 2
  if "$@"; then pass "$label: $what"; else fail "$label: $what (exit=$RC calls=[$CALLS])"; fi
}
has() { grep -aqF -- "$1" "$LOG"; }
hasnot() { ! grep -aqF -- "$1" "$LOG"; }
calls_are() { [ "$CALLS" = "$1" ]; }

echo "== fresh install: per-phase codes + stop at first failure"
fresh_prefix; run fresh-ok "$SREL" "" "$ACC"
expect fresh-ok "exit 0, all 7 phases" bash -c "[ $RC = 0 ] && [ '$CALLS' = 'webview2,database,accounts,services,firewall,backup,start,none,' ]"
fresh_prefix; run fresh-database "$SREL" "database 7" "$ACC"
expect fresh-database "exit 135, nothing after database" bash -c "[ $RC = 135 ] && [ '$CALLS' = 'webview2,database,' ]"
expect fresh-database "abort logged" has "CHECKPOINT_POSTINSTALL_ABORT phase=database exit=135"
fresh_prefix; run fresh-accounts-132 "$SREL" "accounts 132" "$ACC"
expect fresh-accounts-132 "invalid accounts passes 132 through" bash -c "[ $RC = 132 ] && [ '$CALLS' = 'webview2,database,accounts,' ]"
fresh_prefix; run fresh-accounts-1 "$SREL" "accounts 1" "$ACC"
expect fresh-accounts-1 "other accounts failure => 136" bash -c "[ $RC = 136 ]"
fresh_prefix; run fresh-start "$SREL" "start 1" "$ACC"
expect fresh-start "exit 140" bash -c "[ $RC = 140 ]"
expect fresh-start "PowerShell output reaches the Inno log (ExecAndLogOutput)" has "FAKEPS phase=start"

echo "== hooks build: test hook flag does not crash Setup (SetEnvironmentVariable arity)"
fresh_prefix; run hooks-testfailphase "$SHK" "" "$ACC" /TESTFAILPHASE=database
expect hooks-testfailphase "Setup ran (hook env set, fake PS ignores it)" bash -c "[ $RC = 0 ]"
expect hooks-testfailphase "hook logged" has "CHECKPOINT_INTERNAL_TEST_HOOK phase=database"

echo "== upgrade: per-phase codes, F6 no-dump => stays stopped, dump => restore"
for spec in "upgrade-pins 1:141:upgrade-pins," "upgrade-stop 1:142:upgrade-pins,upgrade-stop," "upgrade-backup 1:143:upgrade-pins,upgrade-stop,upgrade-backup,"; do
  fs=${spec%%:*}; rest=${spec#*:}; code=${rest%%:*}; want=${rest#*:}; ph=${fs%% *}
  seed_upgrade; run "up-$ph" "$SREL" "$fs" /SKIPSESSIONCHECK=1 "$PINS"
  expect "up-$ph" "exit $code, no later phase" bash -c "[ $RC = $code ] && [ '$CALLS' = '$want' ]"
  expect "up-$ph" "F6 left-stopped logged" has "CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED reason=no_preupgrade_backup"
  expect "up-$ph" "F6 no restore start" hasnot "CHECKPOINT_POSTINSTALL_UPGRADE_RESTORE start"
  expect "up-$ph" "F6 server binary never started (DB unmigrated)" server_not_started
done
seed_upgrade; run up-start-with-dump "$SREL" "upgrade-start 1" /SKIPSESSIONCHECK=1 "$PINS"
expect up-start-with-dump "exit 144 after all four phases" bash -c "[ $RC = 144 ] && [ '$CALLS' = 'upgrade-pins,upgrade-stop,upgrade-backup,upgrade-start,' ]"
expect up-start-with-dump "restore with dump logged" has "CHECKPOINT_POSTINSTALL_UPGRADE_RESTORE start CheckpointServer dump=C:\\ProgramData\\Checkpoint\\backups\\pre-upgrade-wine-fake.dump"
expect up-start-with-dump "server binary started by the restore" server_started
seed_upgrade
mkdir -p "$RUN/drive_c/ProgramData/Checkpoint/backups" "$RUN/drive_c/ProgramData/Checkpoint/logs"
echo PGDMP-old > "$RUN/drive_c/ProgramData/Checkpoint/backups/pre-upgrade-stale.dump"
printf 'C:\\ProgramData\\Checkpoint\\backups\\pre-upgrade-stale.dump' > "$RUN/drive_c/ProgramData/Checkpoint/logs/preupgrade-dump.current"
run up-stale-marker "$SREL" "upgrade-backup 1" /SKIPSESSIONCHECK=1 "$PINS"
expect up-stale-marker "stale marker from an earlier run is cleared at Setup start => still left stopped" has "CHECKPOINT_POSTINSTALL_UPGRADE_LEFT_STOPPED reason=no_preupgrade_backup"
expect up-stale-marker "stale marker => server binary never started" server_not_started
seed_upgrade; run up-silent-unknown "$SREL" "" "$PINS"
expect up-silent-unknown "silent upgrade, open-session count unknown => 131 + reason" bash -c "[ $RC = 131 ] && grep -q 'CHECKPOINT_OPEN_SESSIONS_SILENT reason=open_session_count_unknown_server_unreachable' '$LOG'"

echo "== kiosk"
fresh_prefix; run kiosk-ok "$KREL" ""
expect kiosk-ok "exit 0" bash -c "[ $RC = 0 ] && [ '$CALLS' = 'kiosk,' ]"
expect kiosk-ok "PowerShell output reaches the Inno log" has "FAKEPS phase=kiosk"
fresh_prefix; run kiosk-fail "$KREL" "kiosk 1"
expect kiosk-fail "exit 150 + abort logged" bash -c "[ $RC = 150 ] && grep -q CHECKPOINT_KIOSK_POSTINSTALL_ABORT '$LOG'"
fresh_prefix; run kiosk-hooks "$KHK" "kiosk 150" /KIOSKPOSTINSTALLFAIL=1
expect kiosk-hooks "hooks build runs, hook logged, exit 150" bash -c "[ $RC = 150 ] && grep -q 'CHECKPOINT_KIOSK_INTERNAL_TEST_HOOK fail=1' '$LOG'"

WINEPREFIX="$RUN" wineserver -k 2>/dev/null || true
echo "SUMMARY pass=$PASSES fail=$FAILS work=$WORK"
[ "$FAILS" -eq 0 ]
