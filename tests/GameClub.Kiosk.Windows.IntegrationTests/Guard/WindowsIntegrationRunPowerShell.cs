#if NET48
#nullable disable
using System;
using System.Diagnostics;
using System.Text;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

internal static class WindowsIntegrationRunPowerShell
{
    internal static string Run(string script, bool tolerateNonZeroExit = false)
    {
        var encoded = Convert.ToBase64String(
            Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'; " + script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("powershell");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit(60_000);
        var errText = stderr.ToString();
        if (!string.IsNullOrWhiteSpace(errText))
        {
            Console.WriteLine($"RunPowerShell stderr exit={proc.ExitCode}: {errText}");
        }

        if (proc.ExitCode != 0 && !tolerateNonZeroExit)
        {
            throw new InvalidOperationException(
                $"PowerShell exit {proc.ExitCode}. stderr: {errText}. script: {script}");
        }

        return stdout.ToString();
    }
}
#endif