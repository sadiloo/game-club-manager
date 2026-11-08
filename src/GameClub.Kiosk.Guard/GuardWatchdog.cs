using System.Diagnostics;

namespace GameClub.Kiosk.Guard;

internal static class GuardWatchdog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Checkpoint",
        "guard",
        "watchdog.log");

    internal static void Run(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        RegistryPolicyStore.EnsureDefaults();
        while (!ct.IsCancellationRequested)
        {
            var kiosk = Process.GetProcessesByName("GameClub.Kiosk");
            if (kiosk.Length == 0 || !KioskPolicyGuard.IsInteractiveSessionKiosk(kiosk))
            {
                TryRestartKiosk("missing_or_wrong_session");
            }

            if (RegistryPolicyStore.Tampered())
            {
                RegistryPolicyStore.Repair();
                File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} guard.policy_tamper{Environment.NewLine}");
            }

            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
    }

    private static void TryRestartKiosk(string reason)
    {
        var path = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_EXE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            KioskPolicyGuard.LaunchInActiveConsoleSession(path);
            File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} guard.restart {reason} {path}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} restart_failed {ex.Message}{Environment.NewLine}");
        }
    }
}
