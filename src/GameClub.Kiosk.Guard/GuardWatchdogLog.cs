using System;
using System.IO;

namespace GameClub.Kiosk.Guard;

internal static class GuardWatchdogLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Checkpoint",
        "guard",
        "watchdog.log");

    internal static void Append(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(LogPath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch
        {
            /* non-fatal */
        }
    }
}