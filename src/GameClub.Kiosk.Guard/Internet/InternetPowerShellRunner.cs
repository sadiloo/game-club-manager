using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Guard;

namespace GameClub.Kiosk.Guard.Internet;

internal static class InternetPowerShellRunner
{
    [ThreadStatic]
    private static bool ReconcilePathThread;

    internal static int RunCallCount;
    internal static int ReconcileRunCallCount;
    internal static int ReconcileRunCallCountAtAbandon = -1;

    internal readonly struct RunTelemetry
    {
        internal RunTelemetry(string output, long psStartMs, long wallMs, string stderr)
        {
            Output = output;
            PsStartMs = psStartMs;
            WallMs = wallMs;
            Stderr = stderr ?? "";
        }

        public string Output { get; }
        public long PsStartMs { get; }
        public long WallMs { get; }
        public string Stderr { get; }
    }

    internal static void SetReconcilePathThread(bool active) => ReconcilePathThread = active;

    internal static void ResetReconcileRunTelemetry()
    {
        Interlocked.Exchange(ref ReconcileRunCallCount, 0);
        Interlocked.Exchange(ref ReconcileRunCallCountAtAbandon, -1);
    }

    private static int _inFlightRootPid;
    private static int _killInFlightGeneration;

    internal static void MarkAbandonBoundary()
    {
        ReconcileRunCallCountAtAbandon = Volatile.Read(ref ReconcileRunCallCount);
        GuardWatchdogLog.Append($"reconcile_abandon_runner_calls={ReconcileRunCallCountAtAbandon}");
        RequestKillInFlight("abandon_boundary");
    }

    internal static void RequestKillInFlight(string reason)
    {
        Interlocked.Increment(ref _killInFlightGeneration);
        var pid = Volatile.Read(ref _inFlightRootPid);
        if (pid <= 0)
        {
            return;
        }

        GuardWatchdogLog.Append($"powershell_kill_inflight reason={reason} pid={pid}");
        InternetPowerShellProcessTree.KillTree(pid);
    }

    private static bool ShouldCancelInFlight(int generationAtStart)
        => generationAtStart != Volatile.Read(ref _killInFlightGeneration)
           || (ReconcilePathThread && IsReconcileAbandoned());

    internal static bool IsReconcileAbandoned()
        => Volatile.Read(ref ReconcileRunCallCountAtAbandon) >= 0;

    internal static string WrapScript(string script) =>
        "$ProgressPreference = 'SilentlyContinue'\n"
        + "$InformationPreference = 'SilentlyContinue'\n"
        + script;

    public static string Run(string script, int timeoutMs = 120_000, string? stepName = null)
        => RunWithTelemetry(script, timeoutMs, stepName).Output;

    internal static RunTelemetry RunWithTelemetry(string script, int timeoutMs = 120_000, string? stepName = null)
    {
        var stepSw = Stopwatch.StartNew();
        var psStartSw = Stopwatch.StartNew();
        Interlocked.Increment(ref RunCallCount);
        if (ReconcilePathThread)
        {
            Interlocked.Increment(ref ReconcileRunCallCount);
        }

        Process? proc = null;
        long psStartMs = 0;
        var stderr = "";
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(WrapScript(script)));
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
            psStartMs = psStartSw.ElapsedMilliseconds;
            var pid = proc.Id;
            Volatile.Write(ref _inFlightRootPid, pid);
            var killGenAtStart = Volatile.Read(ref _killInFlightGeneration);
            var stdoutTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
            var stderrTask = Task.Run(() => proc.StandardError.ReadToEnd());

            var waitDeadline = Environment.TickCount + timeoutMs;
            var exited = false;
            while (Environment.TickCount < waitDeadline)
            {
                if (ShouldCancelInFlight(killGenAtStart))
                {
                    var step = string.IsNullOrWhiteSpace(stepName) ? "powershell" : stepName;
                    GuardWatchdogLog.Append(
                        $"powershell_cancelled step={step} pid={pid} reason=inflight_kill");
                    InternetPowerShellProcessTree.KillTree(pid);
                    return new RunTelemetry(
                        "apply_failed:powershell_cancelled",
                        psStartMs,
                        stepSw.ElapsedMilliseconds,
                        stderr);
                }

                if (proc.WaitForExit(50))
                {
                    exited = true;
                    break;
                }
            }

            if (!exited && !proc.HasExited)
            {
                var step = string.IsNullOrWhiteSpace(stepName) ? "powershell" : stepName;
                GuardWatchdogLog.Append(
                    $"powershell_timeout step={step} pid={pid} budget_ms={timeoutMs}");
                InternetPowerShellProcessTree.KillTree(pid);
                return new RunTelemetry("apply_failed:powershell_timeout", psStartMs, stepSw.ElapsedMilliseconds, stderr);
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult() ?? "";
            var output = InternetPowerShellCompletedOutcome.ResolveOutput(proc.ExitCode, stdout, stderr);
            if (proc.ExitCode != 0)
            {
                return new RunTelemetry(output, psStartMs, stepSw.ElapsedMilliseconds, stderr);
            }

            var stepLabel = string.IsNullOrWhiteSpace(stepName) ? "powershell" : stepName;
            GuardWatchdogLog.Append(
                $"powershell_step step={stepLabel} outcome=ok elapsed_ms={stepSw.ElapsedMilliseconds} budget_ms={timeoutMs}");
            return new RunTelemetry(output, psStartMs, stepSw.ElapsedMilliseconds, stderr);
        }
        catch (Exception ex)
        {
            return new RunTelemetry(
                $"apply_failed:powershell_error:{ex.Message}",
                psStartMs,
                stepSw.ElapsedMilliseconds,
                stderr);
        }
        finally
        {
            if (proc is not null
                && Volatile.Read(ref _inFlightRootPid) == proc.Id)
            {
                Interlocked.Exchange(ref _inFlightRootPid, 0);
            }

            proc?.Dispose();
        }
    }
}