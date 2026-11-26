using System.Diagnostics;
using System.IO;
using GameClub.Kiosk.Core.Security;
using GameClub.Kiosk.Guard.Internet;

namespace GameClub.Kiosk.Guard;

/// <summary>Session-targeted shell recovery — not a global process-name poll.</summary>
internal sealed class KioskShellSupervisor
{
    private readonly KioskGuardIdentity _identity;
    private DateTime _lastRelaunchUtc = DateTime.MinValue;
    private Process? _supervised;
    private uint? _supervisedSessionId;
    private DateTime? _shellGateWaitBeginUtc;

    private static readonly TimeSpan ShellGateMaxWait = TimeSpan.FromSeconds(20);

    internal KioskShellSupervisor(KioskGuardIdentity identity) => _identity = identity;

    internal void Run(CancellationToken ct)
    {
        if (!_identity.IsResolved)
        {
            GuardWatchdogLog.Append("guard.identity_failed");
            return;
        }

        Environment.SetEnvironmentVariable("CHECKPOINT_KIOSK_SID", _identity.Sid);
        TryApplyPolicies("startup");
        var protector = new GuardProcessProtection();
        var protection = new KioskShellProtectionLifecycle(protector, fault => GuardWatchdogLog.Append(fault));
        GuardProcessProtection.ApplyStartupProtection(protection);
        using var trustCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var trustThread = new Thread(() => KioskTrustFileWriter.Run(trustCts.Token))
        {
            IsBackground = true,
            Name = "CheckpointKioskTrust",
        };
        trustThread.Start();
        while (!ct.IsCancellationRequested)
        {
            TryRepairPolicies();

            var sessionId = ConsoleSessionLaunch.FindActiveUserSessionId(_identity.UserName);
            if (sessionId is null)
            {
                GuardWatchdogLog.Append("guard.kiosk_user_session_not_found");
                Wait(ct, TimeSpan.FromSeconds(5));
                continue;
            }

            if (_supervised is { HasExited: false } && _supervisedSessionId == sessionId)
            {
                Wait(ct, TimeSpan.FromSeconds(5));
                continue;
            }

            if (!InternetGuardShellLaunchGate.IsReady)
            {
                _shellGateWaitBeginUtc ??= DateTime.UtcNow;
                var gateWait = DateTime.UtcNow - _shellGateWaitBeginUtc.Value;
                if (gateWait < ShellGateMaxWait)
                {
                    Wait(ct, TimeSpan.FromMilliseconds(250));
                    continue;
                }

                GuardWatchdogLog.Append(
                    $"guard.shell_gate_timeout elapsed_ms={(int)gateWait.TotalMilliseconds}");
                _shellGateWaitBeginUtc = null;
            }
            else
            {
                _shellGateWaitBeginUtc = null;
            }

            if (!EnsureShellRunning(sessionId.Value, protection))
            {
                Wait(ct, TimeSpan.FromSeconds(5));
            }
        }
    }

    private bool EnsureShellRunning(uint sessionId, KioskShellProtectionLifecycle protection)
    {
        if (DateTime.UtcNow - _lastRelaunchUtc < TimeSpan.FromSeconds(5))
        {
            return false;
        }

        var path = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_EXE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        TryApplyPolicies("before_launch");
        if (!ConsoleSessionLaunch.TryLaunch(path, out var detail))
        {
            GuardWatchdogLog.Append($"guard.launch_failed {detail}");
            return false;
        }

        _lastRelaunchUtc = DateTime.UtcNow;
        _supervisedSessionId = sessionId;
        const string launchedPrefix = "launched_pid_";
        if (detail.StartsWith(launchedPrefix, StringComparison.Ordinal)
            && int.TryParse(detail.Substring(launchedPrefix.Length), out var pid))
        {
            try
            {
                _supervised = Process.GetProcessById(pid);
                protection.ProtectShellAfterLaunch(pid);
            }
            catch
            {
                _supervised = null;
            }
        }

        GuardWatchdogLog.Append($"guard.restart {detail}");
        return true;
    }

    // R10j (BL-rr8-1): policy writes need the kiosk hive (HKU\<sid>), which is loaded only once the kiosk user
    // logs on. Before that (fresh install, boot before autologon) skip and retry; a registry failure is logged and
    // never escapes the watchdog thread (an unhandled exception there terminates the CheckpointGuard service).
    private bool _hiveNotLoadedLogged;

    private void TryApplyPolicies(string context)
    {
        try
        {
            if (!RegistryPolicyStore.KioskHiveLoaded())
            {
                LogHiveNotLoadedOnce(context);
                return;
            }

            _hiveNotLoadedLogged = false;
            RegistryPolicyStore.EnsureDefaults();
        }
        catch (Exception ex)
        {
            GuardWatchdogLog.Append($"guard.policy_apply_failed context={context} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TryRepairPolicies()
    {
        try
        {
            if (!RegistryPolicyStore.KioskHiveLoaded())
            {
                LogHiveNotLoadedOnce("loop");
                return;
            }

            _hiveNotLoadedLogged = false;
            if (RegistryPolicyStore.Tampered())
            {
                RegistryPolicyStore.Repair();
                GuardWatchdogLog.Append("guard.policy_tamper");
            }
        }
        catch (Exception ex)
        {
            GuardWatchdogLog.Append($"guard.policy_apply_failed context=loop error={ex.GetType().Name}: {ex.Message}");
        }
    }

    private void LogHiveNotLoadedOnce(string context)
    {
        if (_hiveNotLoadedLogged)
        {
            return;
        }

        _hiveNotLoadedLogged = true;
        GuardWatchdogLog.Append($"guard.kiosk_hive_not_loaded context={context} (policies applied once the kiosk user logs on)");
    }

    private static void Wait(CancellationToken ct, TimeSpan delay)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < delay && !ct.IsCancellationRequested; elapsed += TimeSpan.FromMilliseconds(250))
        {
            Thread.Sleep(250);
        }
    }
}
