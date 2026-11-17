using System.Diagnostics;
using System.Text;

namespace GameClub.Server.Tests;

public sealed class Ss14FixR1fVisibilityNodeTests
{
    private static string ScriptPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..",
        "Scripts", "ss14-manager-visibility-selftest.mjs"));

    [Fact]
    public async Task FixR1f_manager_visibility_jsdom_selftest()
    {
        var script = ScriptPath();
        var scriptDir = Path.GetDirectoryName(script)!;
        Assert.True(File.Exists(script), $"Missing script: {script}");
        var nodeModules = Path.Combine(scriptDir, "node_modules", "jsdom", "package.json");
        if (!File.Exists(nodeModules))
        {
            await RunNpmCiAsync(scriptDir);
        }

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"\"{script}\"",
            WorkingDirectory = scriptDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        Assert.NotNull(proc);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await proc.WaitForExitAsync(cts.Token);
        var stderr = await stderrTask;
        var stdout = await stdoutTask;
        Assert.Equal(0, proc.ExitCode);
        Assert.Contains("ok", stdout, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
    }

    private static async Task RunNpmCiAsync(string scriptDir)
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
        Assert.NotNull(npm);
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

            Assert.Fail($"npm ci timed out after 120s (network?). Output:\n{combined}");
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
            Assert.Fail($"npm ci failed (network?). exit={npm.ExitCode}\n{combined}");
        }
    }
}