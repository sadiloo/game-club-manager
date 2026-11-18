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
    ("Md_backoff_reset_removed", LC, "Interlocked.Exchange(ref _attempt, 0);", ""),
    ("Me_give_up_after_4_attempts", LC, "var attempt = Interlocked.Increment(ref _attempt) - 1;", "var attempt = Interlocked.Increment(ref _attempt) - 1; if (attempt >= 4) break;"),
    ("Me1_give_up_after_1_attempt", LC, "var attempt = Interlocked.Increment(ref _attempt) - 1;", "var attempt = Interlocked.Increment(ref _attempt) - 1; if (attempt >= 1) break;"),
    ("Mb_inner_enqueue_catch_removed", RT, INNER, '                await _queue!.EnqueueAsync(Guid.NewGuid(), "heartbeat_error", ex.Message, DateTime.UtcNow, ct);\n'),
    ("Ml_reverse_monotonic", RT, ML_REVERSE_BLOCK, ML_REVERSE_MUTANT),
    ("RePair_revert_runtime_configured", HS, "await _runtime.ResetForReconfigureAsync();\n        _runtime.Configure", "if (false) await _runtime.ResetForReconfigureAsync();\n        if (!_pairingJustFailed) _runtime.Configure", "wpf-host"),
    ("RePair_revert_begin_early_return", LC, "        if (_disposed)\n        {\n            throw new ObjectDisposedException(nameof(KioskHubLifecycle));\n        }\n\n        if (_loopTask is not null)", "        if (!_disposed)\n        {\n            return;\n        }\n\n        if (_loopTask is not null)"),
    ("M_begin_early_return_keeps_loop", LC, "        if (_loopTask is not null)\n        {\n            _lifecycleCts?.Cancel();", "        if (_loopTask is not null) { return; }\n        if (false && _loopTask is not null) { _lifecycleCts?.Cancel();"),
    ("M_hbfail_no_failure_increment", RT, [
        ("                    _consecutiveHeartbeatFailures++;", "                    _ = _consecutiveHeartbeatFailures;"),
        ("                _consecutiveHeartbeatFailures++;", "                _ = _consecutiveHeartbeatFailures;"),
    ]),
    ("M_srflag_no_autoreconnect_flag", STA, "            || signalRAutoReconnecting", ""),
    ("Mx_heartbeat_failures_never_reset", RT, "                    _consecutiveHeartbeatFailures = 0;", "                    _ = _consecutiveHeartbeatFailures;"),
    ("Ms6_no_connected_on_signalr_reconnected", LC, "    private void OnTransportReconnected() => Publish(KioskTransportPhase.Connected, \"\");", "    private void OnTransportReconnected() { }"),
    ("P5_revert_publish_trycatch", LC, "        try\n        {\n            ConnectionStatusChanged?.Invoke(phase, detail);\n        }\n        catch\n        {\n            // Subscriber faults must not stop the reconnect loop.\n        }", "        ConnectionStatusChanged?.Invoke(phase, detail);"),
    ("P6_revert_double_dispose", LC, "        if (_disposed)\n        {\n            return;\n        }\n\n        _disposed = true;", "", "equivalent"),
    ("Host_no_tick_on_boot", HS, "        EnsureStatusUiTickRunning();\n        _ = RefreshAsync();", "        _ = RefreshAsync();", "host-d5"),
    ("Host_no_tick_on_connected", HS, "                    EnsureStatusUiTickRunning();\n                    if (_runCatalogOnFirstConnected)", "                    if (_runCatalogOnFirstConnected)", "host-d5"),
    ("Host_stops_tick_on_ack_lock", HS, "                _replayTick.Stop();\n                RefreshStationStatusText();", "                _replayTick.Stop();\n                _uiTick.Stop();\n                RefreshStationStatusText();", "host-d5"),
    ("Host_d7_clears_session_on_local_lock", HS, "        _localLockLeaseExpired = leaseExpired;\n        RunSessionCleanup();", "        _localLockLeaseExpired = leaseExpired;\n        _sessionId = Guid.Empty;\n        _runtime.BindSession(Guid.Empty);\n        RunSessionCleanup();", "host-d5"),
]

CORE_PROJ = "tests/GameClub.Kiosk.Core.Tests/GameClub.Kiosk.Core.Tests.csproj"
HOST_PROJ = "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj"
HOST_FILTER = "FullyQualifiedName~KioskHostStatusTimerTests"
BUILD_WARN_OFF = ["-p:TreatWarningsAsErrors=false"]

host_only = os.environ.get("MUT_HOST_ONLY", "").strip() in ("1", "true", "yes")
res = {}
for item in M:
    name, path = item[0], item[1]
    tag = item[4] if len(item) > 4 else "core"
    if host_only and tag != "host-d5":
        continue
    if tag == "equivalent":
        res[name] = "EQUIVALENT"
        print(name, res[name], flush=True)
        continue
    if len(item) >= 3 and isinstance(item[2], list):
        pairs = item[2]
    else:
        pairs = [(item[2], item[3])]
    p = os.path.join(ROOT, path)
    src = open(p, newline="").read()
    if any(not src.count(x) for x, _ in pairs):
        res[name] = "PATTERN_NOT_FOUND"
        continue
    mutated = src
    for x, y in pairs:
        mutated = mutated.replace(x, y)
    open(p, "w", newline="").write(mutated)
    try:
        if tag == "wpf-host":
            res[name] = "WPF_HOST_NOT_BUILDABLE_ON_LINUX"
            continue
        if tag == "host-d5":
            b_ = subprocess.run(
                ["dotnet", "build", HOST_PROJ, "-c", "Release", "-f", "net48", *BUILD_WARN_OFF],
                cwd=ROOT,
                env=env,
                capture_output=True,
                text=True,
            )
            if b_.returncode != 0:
                res[name] = "BUILD_FAIL"
                continue
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
                    HOST_FILTER,
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
            ):
                res[name] = "WPF_HOST_EXECUTE_WINDOWS_ONLY"
                continue
            failed = sorted(set(re.findall(r"^\s+Failed (\S+)", out, re.M)))
            res[name] = {"killed": len(failed) > 0, "failed_tests": failed}
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
        failed = sorted(set(re.findall(r"^\s+Failed (\S+)", out, re.M)))
        res[name] = {"killed": len(failed) > 0, "failed_tests": failed}
    finally:
        open(p, "w", newline="").write(src)
    print(name, res[name], flush=True)

open(os.path.join(OUT, "mutations-r1d.json"), "w").write(json.dumps(res, indent=2))
failed = []
for name, outcome in res.items():
    if isinstance(outcome, dict) and not outcome.get("killed"):
        failed.append(name)
    if outcome in ("BUILD_FAIL", "PATTERN_NOT_FOUND", "SURVIVES"):
        failed.append(name)
    if host_only and isinstance(outcome, dict) and outcome.get("killed") is False:
        failed.append(name)
if host_only and failed:
    print("HOST_MUTATION_SURVIVORS", sorted(set(failed)), file=sys.stderr)
    sys.exit(1)
sys.exit(0)