#if NET48
using System;
using System.Diagnostics;
using System.IO;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>Runs the net10.0 harness_live_database_reset_only test (built by CI before net48 phases).</summary>
internal static class LiveDatabaseResetInvoker
{
    public static void ResetViaHarnessTest()
    {
        var repoRoot = ResolveRepoRoot();
        var project = Path.Combine(repoRoot, "tests/GameClub.Kiosk.Windows.IntegrationTests/GameClub.Kiosk.Windows.IntegrationTests.csproj");
        var args =
            $"test \"{project}\" -c Release -f net10.0 --no-build --nologo "
            + "--filter FullyQualifiedName~harness_live_database_reset_only";
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet test for live DB reset.");
        var err = proc.StandardError.ReadToEnd();
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(120_000);
        Console.WriteLine("LIVE_DB_RESET harness exit=" + proc.ExitCode);
        if (!string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine(output);
        }

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException("live database reset harness failed: " + err);
        }
    }

    private static string ResolveRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "GameClub.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir) ?? dir;
        }

        throw new InvalidOperationException("Could not locate repository root for live DB reset.");
    }
}
#endif