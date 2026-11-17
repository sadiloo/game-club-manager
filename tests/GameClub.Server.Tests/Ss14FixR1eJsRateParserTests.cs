using System.Diagnostics;

namespace GameClub.Server.Tests;

public sealed class Ss14FixR1eJsRateParserTests
{
    private static string ScriptPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..",
        "Scripts", "ss14-shipped-rate-parser-selftest.mjs"));

    [Fact]
    public void FixR1e_shipped_app_js_rate_parser_grouping_via_node()
    {
        var script = ScriptPath();
        Assert.True(File.Exists(script), $"Missing node self-test script: {script}");
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"\"{script}\"",
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