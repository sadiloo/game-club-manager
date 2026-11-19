using System;
using System.Diagnostics;
using System.Text;

namespace GameClub.Kiosk.Guard.Internet;

internal static class InternetPowerShellRunner
{
    public static string Run(string script, int timeoutMs = 120_000)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("powershell start failed");
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(timeoutMs);
            if (proc.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                if (string.IsNullOrWhiteSpace(detail))
                {
                    detail = $"exit_{proc.ExitCode}";
                }

                return detail.StartsWith("apply_failed", StringComparison.Ordinal)
                    ? detail
                    : $"apply_failed:powershell_error:{detail}";
            }

            var tail = stdout.Trim().Split('\n').LastOrDefault()?.Trim();
            return string.IsNullOrEmpty(tail) ? "" : tail!;
        }
        catch (Exception ex)
        {
            return $"apply_failed:powershell_error:{ex.Message}";
        }
    }
}