using System.Diagnostics;
using System.Text;

namespace GameClub.Server.Tests;

internal static class ScriptsNodeTestHelper
{
    public static async Task RunNpmCiAsync(string scriptDir)
    {
        using var npm = Process.Start(new ProcessStartInfo
        {
            FileName = "npm",
            Arguments = "ci --no-fund --no-audit",
            WorkingDirectory = scriptDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        if (npm is null)
        {
            throw new InvalidOperationException("Failed to start npm ci.");
        }

        var stdoutTask = npm.StandardOutput.ReadToEndAsync();
        var stderrTask = npm.StandardError.ReadToEndAsync();
        var combined = new StringBuilder();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await npm.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                npm.Kill(entireProcessTree: true);
            }
            catch
            {
                // best effort
            }

            var partialOut = await stdoutTask;
            var partialErr = await stderrTask;
            if (!string.IsNullOrWhiteSpace(partialOut))
            {
                combined.AppendLine(partialOut.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(partialErr))
            {
                combined.AppendLine(partialErr.TrimEnd());
            }

            throw new InvalidOperationException($"npm ci timed out after 120s (network?). Output:\n{combined}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            combined.AppendLine(stdout.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            combined.AppendLine(stderr.TrimEnd());
        }

        if (npm.ExitCode != 0)
        {
            throw new InvalidOperationException($"npm ci failed (network?). exit={npm.ExitCode}\n{combined}");
        }
    }
}