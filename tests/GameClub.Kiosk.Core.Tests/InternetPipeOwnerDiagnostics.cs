using System.IO.Pipes;
using System.Runtime.CompilerServices;
using GameClub.Kiosk.Core.Guard;

namespace GameClub.Kiosk.Core.Tests;

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
        }
    }

    internal static TestScope BeginTestScope([CallerMemberName] string testName = "")
    {
        WritePipeOwner("test_begin", testName);
        return new TestScope(testName);
    }

    internal static void WriteFixturePhase(string phase, string context)
    {
        WritePipeOwner(phase, context);
    }

    internal static void WritePipeOwner(string phase, string context)
    {
        var reachable = false;
        var detail = "n/a";
        if (OperatingSystem.IsWindows())
        {
            try
            {
                const string pipeRoot = @"\\.\pipe";
                reachable = Directory.EnumerateFiles(pipeRoot).Any(path =>
                    string.Equals(
                        Path.GetFileName(path),
                        GuardInternetClient.PipeName,
                        StringComparison.OrdinalIgnoreCase));
                detail = reachable ? "named_pipe_present" : "named_pipe_missing";
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ":" + ex.Message;
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                var pipePath = Path.Combine("/tmp", GuardInternetClient.PipeName);
                reachable = File.Exists(pipePath);
                detail = reachable ? "unix_socket_present" : "unix_socket_missing";
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ":" + ex.Message;
            }
        }
        else
        {
            detail = "skipped_platform";
        }

        Console.WriteLine(
            $"PIPE_OWNER phase={phase} context={context} pipe={GuardInternetClient.PipeName} "
            + $"exists={reachable} serverPid=0 serverName=n/a serverStartUtc=n/a serverCmd=n/a detail={detail}");
        Console.WriteLine($"GUARD_PROC phase={phase} context={context} note=core_tests_no_guard_procs");
    }
}