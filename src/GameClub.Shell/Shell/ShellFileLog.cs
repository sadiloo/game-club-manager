using System;
using System.IO;
using System.Text;

namespace GameClub.Shell.Shell;

internal static class ShellFileLog
{
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(ShellPaths.ShellLogsDir);
            var path = Path.Combine(ShellPaths.ShellLogsDir, $"shell-{DateTime.Now:yyyyMMdd}.log");
            var line = $"{DateTime.Now:O} {message}{Environment.NewLine}";
            File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch
        {
            // never crash shell on log failure
        }
    }
}