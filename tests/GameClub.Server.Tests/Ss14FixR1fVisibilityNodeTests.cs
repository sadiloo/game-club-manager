using System.Diagnostics;

namespace GameClub.Server.Tests;

public sealed class Ss14FixR1fVisibilityNodeTests
{
    private static string ScriptPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..",
        "Scripts", "ss14-manager-visibility-selftest.mjs"));

    [Fact]
    public void FixR1f_manager_visibility_node_selftest_when_playwright_available()
    {
        var script = ScriptPath();
        var scriptDir = Path.GetDirectoryName(script)!;
        Assert.True(File.Exists(script), $"Missing script: {script}");
        var nodeModules = Path.Combine(scriptDir, "node_modules", "jsdom", "package.json");
        if (!File.Exists(nodeModules))
        {
            using var npm = Process.Start(new ProcessStartInfo
            {
                FileName = "npm",
                Arguments = "install --no-fund --no-audit",
                WorkingDirectory = scriptDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            Assert.NotNull(npm);
            npm.WaitForExit(120_000);
            Assert.Equal(0, npm.ExitCode);
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
        var stderr = proc.StandardError.ReadToEnd();
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(60_000);
        Assert.Equal(0, proc.ExitCode);
        Assert.Contains("ok", stdout, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
    }
}