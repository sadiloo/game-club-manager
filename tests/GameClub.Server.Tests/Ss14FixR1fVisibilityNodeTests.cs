using System.Diagnostics;

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
            await ScriptsNodeTestHelper.RunNpmCiAsync(scriptDir);
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
}