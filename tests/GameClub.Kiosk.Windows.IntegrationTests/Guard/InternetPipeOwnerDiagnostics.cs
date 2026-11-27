#if NET48
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using GameClub.Kiosk.Core.Guard;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>CI pipe-owner and guard-process diagnostics for PipeReconcile / integration fixtures.</summary>
internal static class InternetPipeOwnerDiagnostics
{
    internal sealed class TestScope : IDisposable
    {
        private readonly string _test;
        private bool _disposed;

        internal TestScope(string test) => _test = test;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            WritePipeOwner("test_end", _test);
            WriteGuardProcs("test_end", _test);
        }
    }

    internal static TestScope BeginTestScope([CallerMemberName] string testName = "") => BeginTestScope(testName, null);

    internal static TestScope BeginTestScope(string testName, string startedByStep)
    {
        WriteGuardProcs("test_begin", testName, startedByStep);
        WritePipeOwner("test_begin", testName);
        return new TestScope(testName);
    }

    internal static void WriteCollectionPhase(string phase)
    {
        WriteGuardProcs(phase, "windows-guard-integration-collection");
        WritePipeOwner(phase, "windows-guard-integration-collection");
    }

    internal static void AssertNoStrayGuardOrPipeOwnerAtCollectionStart()
    {
        WriteGuardProcs("fixture_assert_preflight", "windows-guard-integration-collection");
        var owner = WritePipeOwner("fixture_assert_preflight", "windows-guard-integration-collection");
        foreach (var name in new[] { "GameClub.Kiosk.Guard", "GameClub.Kiosk" })
        {
            var procs = Process.GetProcessesByName(name);
            if (procs.Length > 0)
            {
                var pidList = string.Join(",", Array.ConvertAll(procs, p => p.Id.ToString()));
                foreach (var p in procs)
                {
                    p.Dispose();
                }

                FirewallTestConsoleDiagnostics.FailAssertion(
                    $"PIPE_OWNER fixture preflight: unexpected {name} still running (pids={pidList}); "
                    + $"last_owner={owner?.ServerPid} name={owner?.ServerName}");
            }
        }

        if (owner is { PipeReachable: true, ServerPid: > 0 }
            && owner.ServerName is not null
            && owner.ServerName.IndexOf("GameClub.Kiosk.Guard", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            FirewallTestConsoleDiagnostics.FailAssertion(
                $"PIPE_OWNER fixture preflight: pipe {GuardInternetClient.PipeName} owned by Guard pid={owner.ServerPid} "
                + $"cmd={owner.ServerCommandLine}");
        }
    }

    internal static void LogGuardTeardown(Process guard, string step)
    {
        if (guard is null)
        {
            Console.WriteLine($"L17_GUARD_TEARDOWN step={step} guard=null");
            return;
        }

        var exitCode = guard.HasExited ? guard.ExitCode.ToString() : "running";
        DateTime? exitTime = null;
        if (guard.HasExited)
        {
            try
            {
                exitTime = guard.ExitTime.ToUniversalTime();
            }
            catch
            {
                /* access denied on some runners */
            }
        }

        Console.WriteLine(
            $"L17_GUARD_TEARDOWN step={step} pid={guard.Id} hasExited={guard.HasExited} exitCode={exitCode} "
            + $"exitTimeUtc={(exitTime.HasValue ? exitTime.Value.ToString("o") : "n/a")}");
    }

    internal static void WriteGuardProcs(string phase, string context, string startedByStep = null)
    {
        var patterns = new[]
        {
            ("GameClub.Kiosk.Guard", startedByStep),
            ("GameClub.Kiosk", startedByStep),
            ("testhost", context),
            ("powershell", context),
        };
        foreach (var (imageBase, step) in patterns)
        {
            foreach (var proc in Process.GetProcessesByName(imageBase))
            {
                try
                {
                    var startUtc = SafeStartTimeUtc(proc);
                    var cmd = TryReadCommandLine(proc.Id);
                    Console.WriteLine(
                        $"GUARD_PROC phase={phase} context={context} image={imageBase} pid={proc.Id} "
                        + $"startUtc={startUtc} startedBy={step ?? "n/a"} cmd={cmd}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"GUARD_PROC phase={phase} context={context} image={imageBase} pid={proc.Id} error={ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
    }

    internal static PipeOwnerSnapshot WritePipeOwner(string phase, string context)
    {
        var snap = ProbePipeOwner();
        Console.WriteLine(
            $"PIPE_OWNER phase={phase} context={context} pipe={GuardInternetClient.PipeName} "
            + $"exists={snap.PipeReachable} serverPid={snap.ServerPid} serverName={snap.ServerName} "
            + $"serverStartUtc={snap.ServerStartUtc} serverCmd={snap.ServerCommandLine} detail={snap.Detail}");
        return snap;
    }

    internal sealed class PipeOwnerSnapshot
    {
        internal bool PipeReachable;
        internal int ServerPid;
        internal string ServerName;
        internal string ServerStartUtc;
        internal string ServerCommandLine;
        internal string Detail;
    }

    private static PipeOwnerSnapshot ProbePipeOwner()
    {
        var snap = new PipeOwnerSnapshot();
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                GuardInternetClient.PipeName,
                PipeDirection.InOut,
                PipeOptions.None);
            client.Connect(400);
            snap.PipeReachable = true;
            if (GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var serverPid))
            {
                snap.ServerPid = (int)serverPid;
                try
                {
                    using var server = Process.GetProcessById(snap.ServerPid);
                    snap.ServerName = server.ProcessName;
                    snap.ServerStartUtc = SafeStartTimeUtc(server);
                    snap.ServerCommandLine = TryReadCommandLine(snap.ServerPid);
                }
                catch (Exception ex)
                {
                    snap.Detail = $"server_proc:{ex.Message}";
                }
            }
            else
            {
                snap.Detail = $"GetNamedPipeServerProcessId win32={Marshal.GetLastWin32Error()}";
            }
        }
        catch (TimeoutException)
        {
            snap.Detail = "connect_timeout";
        }
        catch (Exception ex)
        {
            snap.Detail = ex.GetType().Name + ":" + ex.Message;
        }

        return snap;
    }

    internal static string SafeStartTimeUtc(Process proc)
    {
        try
        {
            return proc.StartTime.ToUniversalTime().ToString("o");
        }
        catch
        {
            return "n/a";
        }
    }

    internal static string TryReadCommandLine(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    $"-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Process -Filter \\\"ProcessId={pid}\\\").CommandLine\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return "n/a";
            }

            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5_000);
            return string.IsNullOrWhiteSpace(text) ? "n/a" : text;
        }
        catch
        {
            return "n/a";
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);
}
#endif