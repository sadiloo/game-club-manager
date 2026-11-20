using System.Diagnostics;

namespace GameClub.Server.Tests;

public sealed class KioskOpFreeIdleUiSelftestTests
{
    private static string ScriptPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..",
        "Scripts", "kiosk-op-free-idle-selftest.mjs"));

    [Fact]
    public async Task kiosk_op_free_idle_ui_selftest_shows_persian_without_mgr_keys()
    {
        var script = ScriptPath();
        Assert.True(File.Exists(script), $"Missing script: {script}");
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "node",
            Arguments = $"\"{script}\"",
            WorkingDirectory = Path.GetDirectoryName(script)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        Assert.NotNull(proc);
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await proc.WaitForExitAsync(cts.Token);
        Assert.Equal(0, proc.ExitCode);
        Assert.Contains("ok", stdout, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
    }
}