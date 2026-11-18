#!/usr/bin/env python3
import json
import os
import re
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../.."))
OUT = os.environ.get("MUTOUT_DIR", "/tmp/kiosk-reconnect-mut")
os.makedirs(OUT, exist_ok=True)
env = dict(os.environ)
env["PATH"] = os.environ.get("HOME", "") + "/.dotnet:/usr/local/bin:" + os.environ.get("PATH", "")

LC = "src/GameClub.Kiosk.Core/Runtime/KioskHubLifecycle.cs"
RT = "src/GameClub.Kiosk.Core/Runtime/KioskRuntime.cs"
STA = "src/GameClub.Kiosk.Core/Runtime/KioskStationStatus.cs"
HS = "src/GameClub.Kiosk/Services/KioskHostService.cs"
INNER = """                try
                {
                    await _queue!.EnqueueAsync(Guid.NewGuid(), "heartbeat_error", ex.Message, DateTime.UtcNow, ct);
                }
                catch
                {
                }
"""

ML_REVERSE_BLOCK = """        if (phase == KioskTransportPhase.Connected && _monotonicArmedForStart && !_monotonicRestartedForInstance)
        {
            _monotonic.Restart();
            _monotonicArmedForStart = false;
            _monotonicRestartedForInstance = true;
        }"""

ML_REVERSE_MUTANT = """        if (phase == KioskTransportPhase.Connected)
        {
            _monotonic.Restart();
            _ = _monotonicArmedForStart;
            _ = _monotonicRestartedForInstance;
        }"""

M = [
    ("Md_backoff_reset_removed", LC, "Interlocked.Exchange(ref _attempt, 0);", "", "core", None),
    ("Me_give_up_after_4_attempts", LC, "var attempt = Interlocked.Increment(ref _attempt) - 1;", "var attempt = Interlocked.Increment(ref _attempt) - 1; if (attempt >= 4) break;", "core", None),
    ("Me1_give_up_after_1_attempt", LC, "var attempt = Interlocked.Increment(ref _attempt) - 1;", "var attempt = Interlocked.Increment(ref _attempt) - 1; if (attempt >= 1) break;", "core", None),
    ("Mb_inner_enqueue_catch_removed", RT, INNER, '                await _queue!.EnqueueAsync(Guid.NewGuid(), "heartbeat_error", ex.Message, DateTime.UtcNow, ct);\n', "core", None),
    ("Ml_reverse_monotonic", RT, ML_REVERSE_BLOCK, ML_REVERSE_MUTANT, "core", None),
    ("RePair_revert_runtime_configured", HS, "await _runtime.ResetForReconfigureAsync();\n        _runtime.Configure", "if (false) await _runtime.ResetForReconfigureAsync();\n        if (!_pairingJustFailed) _runtime.Configure", "wpf-host", None),
    ("RePair_revert_begin_early_return", LC, "        if (_disposed)\n        {\n            throw new ObjectDisposedException(nameof(KioskHubLifecycle));\n        }\n\n        if (_loopTask is not null)", "        if (!_disposed)\n        {\n            return;\n        }\n\n        if (_loopTask is not null)", "core", None),
    ("M_begin_early_return_keeps_loop", LC, "        if (_loopTask is not null)\n        {\n            _lifecycleCts?.Cancel();", "        if (_loopTask is not null) { return; }\n        if (false && _loopTask is not null) { _lifecycleCts?.Cancel();", "core", None),
    ("M_hbfail_no_failure_increment", RT, [
        ("                    _consecutiveHeartbeatFailures++;", "                    _ = _consecutiveHeartbeatFailures;"),
        ("                _consecutiveHeartbeatFailures++;", "                _ = _consecutiveHeartbeatFailures;"),
    ], None, "core", None),
    ("M_srflag_no_autoreconnect_flag", STA, "            || signalRAutoReconnecting", "", "core", None),
    ("Mx_heartbeat_failures_never_reset", RT, "                    _consecutiveHeartbeatFailures = 0;", "                    _ = _consecutiveHeartbeatFailures;", "core", None),
    ("Ms6_no_connected_on_signalr_reconnected", LC, "    private void OnTransportReconnected() => Publish(KioskTransportPhase.Connected, \"\");", "    private void OnTransportReconnected() { }", "core", None),
    ("P5_revert_publish_trycatch", LC, "        try\n        {\n            ConnectionStatusChanged?.Invoke(phase, detail);\n        }\n        catch\n        {\n            // Subscriber faults must not stop the reconnect loop.\n        }", "        ConnectionStatusChanged?.Invoke(phase, detail);", "core", None),
    ("P6_revert_double_dispose", LC, "        if (_disposed)\n        {\n            return;\n        }\n\n        _disposed = true;", "", "equivalent", None),
    ("Host_no_tick_on_boot", HS, "EnsureStatusUiTickRunning();\n        _ = RefreshAsync();", "        _ = RefreshAsync();", "host-d5", "host_boot_path_shows_no_server_response_when_heartbeats_fail"),
    ("Host_no_tick_on_connected", HS, "EnsureStatusUiTickRunning();\n                    if (_runCatalogOnFirstConnected)", "                    if (_runCatalogOnFirstConnected)", "host-d5", "host_connected_starts_status_tick_without_refresh_async"),
    (
        "Host_stops_tick_on_ack_lock",
        HS,
        r"_replayTick\.Stop\(\);\s*try\s*\{\s*RunSessionCleanup\(\);",
        "_uiTick.Stop();\n        _replayTick.Stop();\n        try\n        {\n            RunSessionCleanup();",
        "host-d5",
        "host_operator_stop_lock_shows_no_server_response_when_heartbeats_fail",
    ),
    (
        "Host_d7_clears_session_on_local_lock",
        HS,
        r"_localLockLeaseExpired = leaseExpired;\s*ShowLockLayout\(\);",
        "_localLockLeaseExpired = leaseExpired;\n        _sessionId = Guid.Empty;\n        _runtime.BindSession(Guid.Empty);\n        ShowLockLayout();",
        "host-d5",
        "host_local_lock_keeps_session_id_bound",
    ),
]

CORE_PROJ = "tests/GameClub.Kiosk.Core.Tests/GameClub.Kiosk.Core.Tests.csproj"
HOST_PROJ = "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj"
BUILD_WARN_OFF = ["-p:TreatWarningsAsErrors=false"]

host_only = os.environ.get("MUT_HOST_ONLY", "").strip() in ("1", "true", "yes")


def ws_apply(text, pattern, replacement, regex=False):
    if regex:
        new, count = re.subn(pattern, replacement, text, count=1, flags=re.MULTILINE | re.DOTALL)
        return new if count else None
    if pattern not in text:
        return None
    return text.replace(pattern, replacement, 1)


def run_host_tests(test_filter):
    b_ = subprocess.run(
        ["dotnet", "build", HOST_PROJ, "-c", "Release", "-f", "net48", *BUILD_WARN_OFF],
        cwd=ROOT,
        env=env,
        capture_output=True,
        text=True,
    )
    if b_.returncode != 0:
        return "BUILD_FAIL", b_.stdout + b_.stderr, []
    t = subprocess.run(
        [
            "dotnet",
            "test",
            HOST_PROJ,
            "-c",
            "Release",
            "-f",
            "net48",
            "--no-build",
            "--filter",
            f"FullyQualifiedName~{test_filter}",
            *BUILD_WARN_OFF,
        ],
        cwd=ROOT,
        env=env,
        capture_output=True,
        text=True,
        timeout=600,
    )
    out = t.stdout + t.stderr
    if (
        t.returncode == 77
        or "platform not supported" in out.lower()
        or "could not find 'mono' host" in out.lower()
        or "test run aborted" in out.lower()
        or "testhost process crashed" in out.lower()
    ):
        return "RUN_ABORTED", out, []
    if t.returncode != 0 and "Failed " not in out:
        return "RUN_FAILED", out, []
    failed = sorted(set(re.findall(r"^\s+Failed (\S+)", out, re.M)))
    if failed:
        return "FAIL", out, failed
    if t.returncode != 0:
        return "RUN_FAILED", out, []
    return "PASS", out, []


REGEX_MUTANTS = {"Host_stops_tick_on_ack_lock", "Host_d7_clears_session_on_local_lock"}

res = {}
failed_names = []

if host_only:
    baseline_status, _, baseline_failed = run_host_tests("KioskHostStatusTimerTests")
    print("BASELINE", baseline_status, baseline_failed, flush=True)
    if baseline_status != "PASS" or baseline_failed:
        print("BASELINE_FAILED", baseline_status, baseline_failed, file=sys.stderr, flush=True)
        sys.exit(1)

for item in M:
    name, path = item[0], item[1]
    tag = item[4] if len(item) > 4 else "core"
    targeted = item[5] if len(item) > 5 else None
    if host_only and tag != "host-d5":
        continue
    if tag == "equivalent":
        res[name] = "EQUIVALENT"
        print(name, res[name], flush=True)
        continue
    p = os.path.join(ROOT, path)
    src = open(p, newline="").read()
    regex = name in REGEX_MUTANTS
    if len(item) >= 3 and isinstance(item[2], list):
        pairs = item[2]
        mutated = src
        ok = True
        for x, y in pairs:
            if x not in mutated:
                ok = False
                break
            mutated = mutated.replace(x, y)
        if not ok:
            res[name] = "PATTERN_NOT_FOUND"
            failed_names.append(name)
            print(name, res[name], flush=True)
            continue
    else:
        pattern = item[2]
        mutated = ws_apply(src, pattern, item[3], regex=regex)
        if mutated is None:
            res[name] = "PATTERN_NOT_FOUND"
            failed_names.append(name)
            print(name, res[name], flush=True)
            continue
    open(p, "w", newline="").write(mutated)
    try:
        if tag == "wpf-host":
            res[name] = "WPF_HOST_NOT_BUILDABLE_ON_LINUX"
            continue
        if tag == "host-d5":
            filt = targeted or "KioskHostStatusTimerTests"
            status, out, failed_tests = run_host_tests(filt)
            if status == "FAIL" and (not targeted or any(targeted in f for f in failed_tests)):
                res[name] = {"result": "KILLED", "failed_tests": failed_tests}
            elif status == "PASS":
                res[name] = "SURVIVES"
                failed_names.append(name)
            else:
                res[name] = status
                failed_names.append(name)
            print(name, res[name], flush=True)
            continue

        b_ = subprocess.run(
            ["dotnet", "build", CORE_PROJ, "-c", "Release", *BUILD_WARN_OFF],
            cwd=ROOT,
            env=env,
            capture_output=True,
            text=True,
        )
        if b_.returncode != 0:
            res[name] = "BUILD_FAIL"
            failed_names.append(name)
            continue
        t = subprocess.run(
            [
                "dotnet",
                "test",
                CORE_PROJ,
                "-c",
                "Release",
                "--no-build",
                "--blame-hang-timeout",
                "90s",
                "--logger",
                f"trx;LogFileName={OUT}/{name}.trx",
                *BUILD_WARN_OFF,
            ],
            cwd=ROOT,
            env=env,
            capture_output=True,
            text=True,
            timeout=600,
        )
        out = t.stdout + t.stderr
        if "test run aborted" in out.lower() or t.returncode == 124:
            res[name] = "RUN_ABORTED"
            failed_names.append(name)
        else:
            failed = sorted(set(re.findall(r"^\s+Failed (\S+)", out, re.M)))
            res[name] = {"result": "KILLED" if failed else "SURVIVES", "failed_tests": failed}
            if not failed:
                failed_names.append(name)
    finally:
        open(p, "w", newline="").write(src)

open(os.path.join(OUT, "mutations-r1d.json"), "w").write(json.dumps(res, indent=2))

if host_only and failed_names:
    print("HOST_MUTATION_SURVIVORS", sorted(set(failed_names)), file=sys.stderr)
    sys.exit(1)
if failed_names and not host_only:
    sys.exit(1)
sys.exit(0)