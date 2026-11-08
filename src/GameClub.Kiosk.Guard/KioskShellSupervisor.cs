using System.Diagnostics;
using System.IO;

namespace GameClub.Kiosk.Guard;

/// <summary>Session-targeted shell recovery — not a global process-name poll.</summary>
internal sealed class KioskShellSupervisor
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Checkpoint",
        "guard",
        "watchdog.log");

    private readonly KioskGuardIdentity _identity;
    private DateTime _lastRelaunchUtc = DateTime.MinValue;
    private Process? _supervised;
    private uint? _supervisedSessionId;

    internal KioskShellSupervisor(KioskGuardIdentity identity) => _identity = identity;

    internal void Run(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        if (!_identity.IsResolved)
        {
            File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} guard.identity_failed{Environment.NewLine}");
            return;
        }

        Environment.SetEnvironmentVariable("CHECKPOINT_KIOSK_SID", _identity.Sid);
        RegistryPolicyStore.EnsureDefaults();
        while (!ct.IsCancellationRequested)
        {
            if (RegistryPolicyStore.Tampered())
            {
                RegistryPolicyStore.Repair();
                File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} guard.policy_tamper{Environment.NewLine}");
            }

            var sessionId = ConsoleSessionLaunch.FindActiveUserSessionId(_identity.UserName);
            if (sessionId is null)
            {
                File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} guard.kiosk_user_session_not_found{Environment.NewLine}");
                Wait(ct, TimeSpan.FromSeconds(5));
                continue;
            }

            if (_supervised is { HasExited: false } && _supervisedSessionId == sessionId)
            {
                Wait(ct, TimeSpan.FromSeconds(5));
                continue;
            }

            if (!EnsureShellRunning(sessionId.Value))
            {
                Wait(ct, TimeSpan.FromSeconds(5));
            }
        }
    }

    private bool EnsureShellRunning(uint sessionId)
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

        RegistryPolicyStore.EnsureDefaults();
        if (!ConsoleSessionLaunch.TryLaunch(path, out var detail))
        {
            File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} guard.launch_failed {detail}{Environment.NewLine}");
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
            }
            catch
            {
                _supervised = null;
            }
        }

        File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} guard.restart {detail}{Environment.NewLine}");
        return true;
    }

    private static void Wait(CancellationToken ct, TimeSpan delay)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < delay && !ct.IsCancellationRequested; elapsed += TimeSpan.FromMilliseconds(250))
        {
            Thread.Sleep(250);
        }
    }
}
