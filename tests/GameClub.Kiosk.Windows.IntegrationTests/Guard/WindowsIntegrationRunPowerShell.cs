#if NET48
#nullable disable
using System;
using System.Diagnostics;
using System.Text;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

internal static class WindowsIntegrationRunPowerShell
{
    internal readonly struct PowerShellRunResult
    {
        public PowerShellRunResult(int exitCode, string stdOut, string stdErr)
        {
            ExitCode = exitCode;
            StdOut = stdOut;
            StdErr = stdErr;
        }

        public int ExitCode { get; }

        public string StdOut { get; }

        public string StdErr { get; }
    }

    internal static string Run(string script, bool tolerateNonZeroExit = false)
    {
        var result = RunResult(script);
        if (result.ExitCode != 0 && !tolerateNonZeroExit)
        {
            throw new InvalidOperationException(
                $"PowerShell exit {result.ExitCode}. stdout: {result.StdOut}. stderr: {result.StdErr}. script: {script}");
        }

        return result.StdOut;
    }

    internal static PowerShellRunResult RunResult(string script)
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
        if (!proc.WaitForExit(60_000))
        {
            try
            {
                proc.Kill();
            }
            catch
            {
                // best effort
            }

            throw new TimeoutException($"PowerShell timed out after 60s. script: {script}");
        }

        var errText = stderr.ToString();
        if (!string.IsNullOrWhiteSpace(errText))
        {
            Console.WriteLine($"RunPowerShell stderr exit={proc.ExitCode}: {errText}");
        }

        return new PowerShellRunResult(proc.ExitCode, stdout.ToString(), errText);
    }
}
#endif