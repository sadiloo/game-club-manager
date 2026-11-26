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
    ("Md_backoff_reset_removed", LC, "Interlocked.Exchange(ref _attempt, 0);", "", "core", "KioskReconnectCoreTests"),
    ("Me_give_up_after_4_attempts", LC, "var attempt = Interlocked.Increment(ref _attempt) - 1;", "var attempt = Interlocked.Increment(ref _attempt) - 1; if (attempt >= 4) break;", "core", "KioskReconnectCoreTests"),
    ("Me1_give_up_after_1_attempt", LC, "var attempt = Interlocked.Increment(ref _attempt) - 1;", "var attempt = Interlocked.Increment(ref _attempt) - 1; if (attempt >= 1) break;", "core", "KioskReconnectCoreTests"),
    ("Mb_inner_enqueue_catch_removed", RT, INNER, '                await _queue!.EnqueueAsync(Guid.NewGuid(), "heartbeat_error", ex.Message, DateTime.UtcNow, ct);\n', "core", "KioskReconnectCoreTests"),
    ("Ml_reverse_monotonic", RT, ML_REVERSE_BLOCK, ML_REVERSE_MUTANT, "core", "KioskReconnectCoreTests"),
    ("RePair_revert_runtime_configured", HS, "await _runtime.ResetForReconfigureAsync();\n        _runtime.Configure", "if (false) await _runtime.ResetForReconfigureAsync();\n        if (!_pairingJustFailed) _runtime.Configure", "wpf-host", None),
    ("RePair_revert_begin_early_return", LC, "        if (_disposed)\n        {\n            throw new ObjectDisposedException(nameof(KioskHubLifecycle));\n        }\n\n        if (_loopTask is not null)", "        if (!_disposed)\n        {\n            return;\n        }\n\n        if (_loopTask is not null)", "core", "KioskReconnectCoreTests"),
    ("M_begin_early_return_keeps_loop", LC, "        if (_loopTask is not null)\n        {\n            _lifecycleCts?.Cancel();", "        if (_loopTask is not null) { return; }\n        if (false && _loopTask is not null) { _lifecycleCts?.Cancel();", "core", "KioskReconnectCoreTests"),
    ("M_hbfail_no_failure_increment", RT, [
        ("                    _consecutiveHeartbeatFailures++;", "                    _ = _consecutiveHeartbeatFailures;"),
        ("                _consecutiveHeartbeatFailures++;", "                _ = _consecutiveHeartbeatFailures;"),
    ], None, "core", "KioskReconnectCoreTests"),
    ("M_srflag_no_autoreconnect_flag", STA, "            || signalRAutoReconnecting", "", "core", "KioskReconnectCoreTests"),
    ("Mx_heartbeat_failures_never_reset", RT, "                    _consecutiveHeartbeatFailures = 0;", "                    _ = _consecutiveHeartbeatFailures;", "core", "KioskReconnectCoreTests"),
    ("Ms6_no_connected_on_signalr_reconnected", LC, "    private void OnTransportReconnected() => Publish(KioskTransportPhase.Connected, \"\");", "    private void OnTransportReconnected() { }", "core", "KioskReconnectCoreTests"),
    ("P5_revert_publish_trycatch", LC, "        try\n        {\n            ConnectionStatusChanged?.Invoke(phase, detail);\n        }\n        catch\n        {\n            // Subscriber faults must not stop the reconnect loop.\n        }", "        ConnectionStatusChanged?.Invoke(phase, detail);", "core", "KioskReconnectCoreTests"),
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
    (
        "Host_logout_cleanup_first",
        HS,
        "var sessionForLogout = _sessionId;\n        ShowLockLayout();",
        "var sessionForLogout = _sessionId;\n        RunSessionCleanup();\n        ShowLockLayout();",
        "host-d5",
        "host_logout_shows_layout_when_session_cleanup_throws",
    ),
    (
        "Host_logout_http_before_lock",
        HS,
        r"var sessionForLogout = _sessionId;\n        ShowLockLayout\(\);\n        _replayTick\.Stop\(\);\n        EnsureStatusUiTickRunning\(\);",
        "var sessionForLogout = _sessionId;",
        "host-d5",
        "host_logout_open_ack_during_slow_post_stays_locked_without_session",
    ),
    (
        "Host_logout_no_guard_before_http",
        HS,
        r"if \(sessionForLogout != Guid\.Empty\)\n        \{\n            _locallyLockedSessionId = sessionForLogout;\n            _localLockLeaseExpired = false;\n            RefreshStationStatusText\(\);\n        \}\n\n        if \(sessionForLogout",
        "if (sessionForLogout",
        "host-d5",
        "host_logout_open_ack_during_slow_post_stays_locked_without_session",
    ),
    (
        "Host_logout_no_post_await_relock",
        HS,
        "        ShowLockLayout();\n        if (!logoutHttpSucceeded)",
        "        if (!logoutHttpSucceeded)",
        "host-d5",
        "host_logout_login_during_slow_post_readopts_new_session_on_next_ack",
    ),
    (
        "Host_logout_real_http_always_success",
        HS,
        "logoutHttpSucceeded = response.IsSuccessStatusCode;",
        "logoutHttpSucceeded = true;",
        "host-d5",
        "host_logout_real_http_409_re_adopts_open_session_on_next_ack",
    ),
    (
        "Host_no_top_up_unlock",
        HS,
        r"if \(sameLockedSession && phase == KioskShellPhase\.Playing && !_logoutInProgress\)\s*\{\s*_locallyLockedSessionId = Guid\.Empty;\s*\}",
        "if (sameLockedSession && phase == KioskShellPhase.Playing)\n                {\n                    _ = _locallyLockedSessionId;\n                }",
        "host-d5",
        "host_top_up_open_ack_after_lease_lock_resumes_playing",
    ),
]

REGEX_MUTANTS = {
    "Host_stops_tick_on_ack_lock",
    "Host_d7_clears_session_on_local_lock",
    "Host_no_top_up_unlock",
    "Host_logout_http_before_lock",
    "Host_logout_no_guard_before_http",
}

CORE_PROJ = "tests/GameClub.Kiosk.Core.Tests/GameClub.Kiosk.Core.Tests.csproj"
HOST_PROJ = "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj"
BUILD_WARN_OFF = ["-p:TreatWarningsAsErrors=false"]

host_only = os.environ.get("MUT_HOST_ONLY", "").strip() in ("1", "true", "yes")


def norm_newlines(text):
    return text.replace("\r\n", "\n")


def read_src(path):
    with open(path, newline="") as f:
        return f.read()


def write_src(path, text):
    with open(path, "w", newline="") as f:
        f.write(text)


def count_pattern_in_src(src, item):
    name = item[0]
    regex = name in REGEX_MUTANTS
    if len(item) >= 3 and isinstance(item[2], list):
        text_n = norm_newlines(src)
        counts = []
        for x, _y in item[2]:
            xn = norm_newlines(x)
            counts.append(text_n.count(xn))
        return counts, all(c == 1 for c in counts)
    pattern_n = norm_newlines(item[2])
    text_n = norm_newlines(src)
    if regex:
        return [len(re.findall(pattern_n, text_n, flags=re.MULTILINE | re.DOTALL))], len(re.findall(pattern_n, text_n, flags=re.MULTILINE | re.DOTALL)) == 1
    return [text_n.count(pattern_n)], text_n.count(pattern_n) == 1


def print_host_mutant_pattern_table():
    hs_path = os.path.join(ROOT, HS)
    hs_src = read_src(hs_path)
    print("=== Host mutant pattern match counts (expect 1) ===", flush=True)
    for item in M:
        tag = item[4] if len(item) > 4 else "core"
        if tag != "host-d5":
            continue
        counts, ok = count_pattern_in_src(hs_src, item)
        killer = item[5] if len(item) > 5 else ""
        flag = "ok" if ok else "MISMATCH"
        print(f"{item[0]:40} counts={counts} killer={killer} {flag}", flush=True)


def ws_apply(text, pattern, replacement, regex=False):
    text_n = norm_newlines(text)
    pattern_n = norm_newlines(pattern)
    replacement_n = norm_newlines(replacement) if replacement is not None else ""
    if regex:
        new, count = re.subn(pattern_n, replacement_n, text_n, count=1, flags=re.MULTILINE | re.DOTALL)
        return new if count else None
    if pattern_n not in text_n:
        return None
    return text_n.replace(pattern_n, replacement_n, 1)


def trx_failure_messages(trx_path):
    if not os.path.isfile(trx_path):
        return []
    try:
        import xml.etree.ElementTree as ET

        root = ET.parse(trx_path).getroot()
        ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
        messages = []
        for result in root.findall(".//t:UnitTestResult[@outcome='Failed']", ns):
            name = result.get("testName") or result.get("name") or "unknown"
            msg_el = result.find(".//t:Message", ns)
            msg = (msg_el.text or "").strip() if msg_el is not None else ""
            messages.append(f"{name}: {msg}")
        return messages
    except Exception as ex:
        return [f"trx_parse_error: {ex}"]


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
    trx_path = os.path.join(OUT, f"baseline-{test_filter.replace('.', '-')}.trx")
    try:
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
                "--results-directory",
                OUT,
                "--logger",
                f"trx;LogFileName={os.path.basename(trx_path)}",
                *BUILD_WARN_OFF,
            ],
            cwd=ROOT,
            env=env,
            capture_output=True,
            text=True,
            timeout=600,
        )
    except subprocess.TimeoutExpired as te:
        return "TIMEOUT", (te.stdout or "") + (te.stderr or ""), []
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


def run_core_tests(test_filter=None):
    b_ = subprocess.run(
        ["dotnet", "build", CORE_PROJ, "-c", "Release", *BUILD_WARN_OFF],
        cwd=ROOT,
        env=env,
        capture_output=True,
        text=True,
    )
    if b_.returncode != 0:
        return "BUILD_FAIL", b_.stdout + b_.stderr, []
    args = [
        "dotnet",
        "test",
        CORE_PROJ,
        "-c",
        "Release",
        "--no-build",
        "--blame-hang-timeout",
        "90s",
        *BUILD_WARN_OFF,
    ]
    if test_filter:
        args.extend(["--filter", f"FullyQualifiedName~{test_filter}"])
    try:
        t = subprocess.run(
            args,
            cwd=ROOT,
            env=env,
            capture_output=True,
            text=True,
            timeout=600,
        )
    except subprocess.TimeoutExpired as te:
        return "TIMEOUT", (te.stdout or "") + (te.stderr or ""), []
    out = t.stdout + t.stderr
    if "test run aborted" in out.lower():
        return "RUN_ABORTED", out, []
    failed = sorted(set(re.findall(r"^\s+Failed (\S+)", out, re.M)))
    if failed:
        return "FAIL", out, failed
    if t.returncode != 0:
        return "RUN_FAILED", out, []
    return "PASS", out, []


res = {}
failed_names = []


def write_mutations_json():
    open(os.path.join(OUT, "mutations-r1d.json"), "w").write(json.dumps(res, indent=2))


print_host_mutant_pattern_table()

if host_only:
    baseline_status, _, baseline_failed = run_host_tests("KioskHostStatusTimerTests")
    print("BASELINE", baseline_status, baseline_failed, flush=True)
    res["baseline"] = {"scope": "host", "status": baseline_status, "failed_tests": baseline_failed}
    if baseline_status != "PASS" or baseline_failed:
        print("BASELINE_FAILED", baseline_status, baseline_failed, file=sys.stderr, flush=True)
        for line in trx_failure_messages(os.path.join(OUT, "baseline-KioskHostStatusTimerTests.trx")):
            print("BASELINE_TRX", line, file=sys.stderr, flush=True)
        write_mutations_json()
        sys.exit(1)
else:
    baseline_status, _, baseline_failed = run_core_tests()
    print("CORE_BASELINE", baseline_status, baseline_failed, flush=True)
    res["baseline"] = {"scope": "core", "status": baseline_status, "failed_tests": baseline_failed}
    if baseline_status != "PASS" or baseline_failed:
        print("CORE_BASELINE_FAILED", baseline_status, baseline_failed, file=sys.stderr, flush=True)
        write_mutations_json()
        sys.exit(1)

for item in M:
    name, path = item[0], item[1]
    tag = item[4] if len(item) > 4 else "core"
    targeted = item[5] if len(item) > 5 else None
    if host_only and tag != "host-d5":
        continue
    if not host_only and tag in ("host-d5", "wpf-host"):
        res[name] = "HOST_WINDOWS_ONLY"
        print(name, res[name], flush=True)
        continue
    if tag == "equivalent":
        res[name] = "EQUIVALENT"
        print(name, res[name], flush=True)
        continue
    p = os.path.join(ROOT, path)
    src = read_src(p)
    regex = name in REGEX_MUTANTS
    if len(item) >= 3 and isinstance(item[2], list):
        pairs = item[2]
        mutated = norm_newlines(src)
        ok = True
        for x, y in pairs:
            xn, yn = norm_newlines(x), norm_newlines(y)
            if xn not in mutated:
                ok = False
                break
            mutated = mutated.replace(xn, yn)
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
    write_src(p, mutated)
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

        status, out, failed_tests = run_core_tests(targeted)
        if status == "FAIL":
            res[name] = {"result": "KILLED", "failed_tests": failed_tests}
        elif status == "PASS":
            res[name] = "SURVIVES"
            failed_names.append(name)
        else:
            res[name] = status
            failed_names.append(name)
        print(name, res[name], flush=True)
    finally:
        write_src(p, src)

write_mutations_json()

if host_only and failed_names:
    print("HOST_MUTATION_SURVIVORS", sorted(set(failed_names)), file=sys.stderr)
    sys.exit(1)
if failed_names and not host_only:
    sys.exit(1)
sys.exit(0)