using System.Diagnostics;
using System.Text;

namespace GameClub.Server.Tests;

public sealed class InpageFormsNodeSelftestTests
{
    private static string ScriptsDir() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..",
        "Scripts"));

    private static IEnumerable<string> SelftestScripts() =>
    [
        "cp-parse-money-selftest.mjs",
        "cp-parse-money-reasons-selftest.mjs",
        "cp-parse-integer-selftest.mjs",
        "cp-layer-selftest.mjs",
        "cp-error-map-selftest.mjs",
        "cp-flows-f01-f17-selftest.mjs",
        "cp-pick-xss-selftest.mjs",
        "cp-positive-number-selftest.mjs",
        "cp-f17-empty-zero-selftest.mjs",
        "cp-shift-close-money-selftest.mjs",
        "cp-money-junk-selftest.mjs",
        "cp-prompt-submit-bodies-selftest.mjs",
        "cp-layer-keydown-selftest.mjs",
        "cp-jalali-leap-selftest.mjs",
        "cp-money-forms-qa19-selftest.mjs",
        "cp-d2-wallet-keydown-regression-selftest.mjs",
        "cp-d3-confirm-alert-selftest.mjs",
        "cp-d4-popstate-back-selftest.mjs",
        "cp-d1-bill-preview-bounded-selftest.mjs",
        "cp-d1-bill-preview-draft-key-guard-selftest.mjs",
        "cp-r14-ks1-preview-change-selftest.mjs",
        "cp-d5-xss-render-selftest.mjs",
        "cp-f12-birthday-percent-selftest.mjs",
        "cp-native-dialog-guard-selftest.mjs",
        "cp-operator-narrow-viewport-selftest.mjs",
    ];

    [Fact]
    public async Task inpage_forms_jsdom_selftests_all_pass()
    {
        var scriptDir = ScriptsDir();
        var nodeModules = Path.Combine(scriptDir, "node_modules", "jsdom", "package.json");
        Assert.True(File.Exists(nodeModules), "jsdom must be installed under Scripts/node_modules (run npm ci in Scripts).");

        foreach (var scriptName in SelftestScripts())
        {
            var script = Path.Combine(scriptDir, scriptName);
            Assert.True(File.Exists(script), $"Missing script: {script}");

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

}