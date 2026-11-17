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
env["PATH"] = os.environ.get("HOME", "") + "/.dotnet:" + os.environ.get("PATH", "")

LC = "src/GameClub.Kiosk.Core/Runtime/KioskHubLifecycle.cs"
RT = "src/GameClub.Kiosk.Core/Runtime/KioskRuntime.cs"
HS = "src/GameClub.Kiosk/Services/KioskHostService.cs"
INNER = """                try
                {
                    await _queue!.EnqueueAsync(Guid.NewGuid(), "heartbeat_error", ex.Message, DateTime.UtcNow, ct);
                }
                catch
                {
                }
"""

M = [
    ("Md_backoff_reset_removed", LC, "Interlocked.Exchange(ref _attempt, 0);", ""),
    ("Me_give_up_after_4_attempts", LC, "var attempt = Interlocked.Increment(ref _attempt) - 1;", "var attempt = Interlocked.Increment(ref _attempt) - 1; if (attempt >= 4) break;"),
    ("Me1_give_up_after_1_attempt", LC, "var attempt = Interlocked.Increment(ref _attempt) - 1;", "var attempt = Interlocked.Increment(ref _attempt) - 1; if (attempt >= 1) break;"),
    ("Mb_inner_enqueue_catch_removed", RT, INNER, '                await _queue!.EnqueueAsync(Guid.NewGuid(), "heartbeat_error", ex.Message, DateTime.UtcNow, ct);\n'),
    ("Ml_reverse_monotonic_every_connected", RT, "        if (phase == KioskTransportPhase.Connected && _monotonicArmedForStart && !_monotonicRestartedForInstance)", "        if (phase == KioskTransportPhase.Connected)"),
    ("Ml_reverse_monotonic_every_connected_2", RT, "            _monotonicArmedForStart = false;\n            _monotonicRestartedForInstance = true;", ""),
    ("RePair_revert_runtime_configured", HS, "await _runtime.ResetForReconfigureAsync();\n        _runtime.Configure", "if (false) await _runtime.ResetForReconfigureAsync();\n        if (!_pairingJustFailed) _runtime.Configure"),
    ("RePair_revert_begin_early_return", LC, "public async Task BeginAsync(CancellationToken parentCancellation)", "public void Begin(CancellationToken parentCancellation)"),
    ("P5_revert_publish_trycatch", LC, "        try\n        {\n            ConnectionStatusChanged?.Invoke(phase, detail);\n        }\n        catch\n        {\n            // Subscriber faults must not stop the reconnect loop.\n        }", "        ConnectionStatusChanged?.Invoke(phase, detail);"),
    ("P6_revert_double_dispose", LC, "        if (_disposed)\n        {\n            return;\n        }\n\n        _disposed = true;", ""),
]

proj = "tests/GameClub.Kiosk.Core.Tests/GameClub.Kiosk.Core.Tests.csproj"
res = {}
for name, path, a, b in M:
    p = os.path.join(ROOT, path)
    src = open(p, newline="").read()
    if isinstance(a, list):
        pairs = a
    else:
        pairs = [(a, b)]
    if any(not src.count(x) for x, _ in pairs):
        res[name] = "PATTERN_NOT_FOUND"
        continue
    mutated = src
    for x, y in pairs:
        mutated = mutated.replace(x, y)
    open(p, "w", newline="").write(mutated)
    try:
        b_ = subprocess.run(["dotnet", "build", proj, "-c", "Release"], cwd=ROOT, env=env, capture_output=True, text=True)
        if b_.returncode != 0:
            res[name] = "BUILD_FAIL"
            continue
        t = subprocess.run(
            ["dotnet", "test", proj, "-c", "Release", "--no-build", "--blame-hang-timeout", "90s", "--logger", f"trx;LogFileName={OUT}/{name}.trx"],
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

open(os.path.join(OUT, "mutations-r1.json"), "w").write(json.dumps(res, indent=2))
sys.exit(0)