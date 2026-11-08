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
        while (!ct.IsCancellationRequested)
        {
            var kiosk = Process.GetProcessesByName("GameClub.Kiosk");
            if (kiosk.Length == 0)
            {
                TryRestartKiosk();
            }

            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
    }

    private static void TryRestartKiosk()
    {
        var path = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_EXE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} restart {path}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} restart_failed {ex.Message}{Environment.NewLine}");
        }
    }
}
