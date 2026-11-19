using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameClub.Kiosk.Guard;

namespace GameClub.Kiosk.Guard.Internet;

internal static class InternetPowerShellRunner
{
    [ThreadStatic]
    private static bool ReconcilePathThread;

    internal static int RunCallCount;
    internal static int ReconcileRunCallCount;
    internal static int ReconcileRunCallCountAtAbandon = -1;

    internal static void SetReconcilePathThread(bool active) => ReconcilePathThread = active;

    internal static void ResetReconcileRunTelemetry()
    {
        Interlocked.Exchange(ref ReconcileRunCallCount, 0);
        Interlocked.Exchange(ref ReconcileRunCallCountAtAbandon, -1);
    }

    internal static void MarkAbandonBoundary()
    {
        ReconcileRunCallCountAtAbandon = Volatile.Read(ref ReconcileRunCallCount);
        GuardWatchdogLog.Append($"reconcile_abandon_runner_calls={ReconcileRunCallCountAtAbandon}");
    }

    public static string Run(string script, int timeoutMs = 120_000, string? stepName = null)
    {
        Interlocked.Increment(ref RunCallCount);
        if (ReconcilePathThread)
        {
            Interlocked.Increment(ref ReconcileRunCallCount);
        }
        Process? proc = null;
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
            proc = Process.Start(psi) ?? throw new InvalidOperationException("powershell start failed");
            var pid = proc.Id;
            var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
            var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

            if (!proc.WaitForExit(timeoutMs))
            {
                var step = string.IsNullOrWhiteSpace(stepName) ? "powershell" : stepName;
                GuardWatchdogLog.Append(
                    $"powershell_timeout step={step} pid={pid} budget_ms={timeoutMs}");
                InternetPowerShellProcessTree.KillTree(pid);
                InternetPowerShellProcessTree.WaitForProcessExit(
                    pid,
                    InternetPowerShellProcessTree.KillAfterExitWaitMsBudget);
                return "apply_failed:powershell_timeout";
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
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
        finally
        {
            proc?.Dispose();
        }
    }
}