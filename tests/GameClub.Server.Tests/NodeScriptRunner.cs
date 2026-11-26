using System.Diagnostics;
using System.Text;

namespace GameClub.Server.Tests;

/// <summary>
/// Shared runner for the node/jsdom self-test scripts under Scripts/ (DEF-18).
/// <see cref="EnsureNodeModulesAsync"/> runs <c>npm ci</c> at most once per test process (thread-safe) before ANY node
/// script starts, so a fresh checkout works and no test can start node while npm is rewriting node_modules.
/// Every failure message carries the exit code, stdout and stderr of node (or npm).
/// </summary>
internal static class NodeScriptRunner
{
    private static readonly object Gate = new();
    private static Task<string?>? _npmCi;

    public static string ScriptsDir() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Scripts"));

    public static string Script(string name) => Path.Combine(ScriptsDir(), name);

    /// <summary>Returns null when node_modules is ready, otherwise the npm failure text.</summary>
    public static Task<string?> EnsureNodeModulesAsync()
    {
        lock (Gate)
        {
            return _npmCi ??= Task.Run(RunNpmCiOnceAsync);
        }
    }

    private static async Task<string?> RunNpmCiOnceAsync()
    {
        var dir = ScriptsDir();
        var marker = Path.Combine(dir, "node_modules", ".package-lock.json");
        var jsdom = Path.Combine(dir, "node_modules", "jsdom", "package.json");
        if (File.Exists(marker) && File.Exists(jsdom)
            && File.GetLastWriteTimeUtc(marker) >= File.GetLastWriteTimeUtc(Path.Combine(dir, "package-lock.json")))
        {
            return null;
        }

        var npm = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        var r = await RunAsync(npm, ["ci", "--no-fund", "--no-audit"], dir, TimeSpan.FromSeconds(120));
        return r.ExitCode == 0 ? null : $"npm ci in {dir} failed: {r.Describe()}";
    }

    public static async Task<NodeResult> RunScriptAsync(string script, TimeSpan timeout, string? workingDirectory = null)
    {
        Assert.True(File.Exists(script), $"Missing node self-test script: {script}");
        var npmFailure = await EnsureNodeModulesAsync();
        Assert.True(npmFailure is null, npmFailure);
        return await RunAsync("node", [script], workingDirectory ?? Path.GetDirectoryName(script)!, timeout);
    }

    public static void AssertOk(NodeResult r, string expectedStdout, bool requireEmptyStderr = true)
    {
        Assert.True(r.ExitCode == 0, $"node exited {r.ExitCode}. {r.Describe()}");
        Assert.True(r.Stdout.Contains(expectedStdout, StringComparison.Ordinal), $"node stdout lacks '{expectedStdout}'. {r.Describe()}");
        if (requireEmptyStderr)
        {
            Assert.True(string.IsNullOrWhiteSpace(r.Stderr), $"node wrote to stderr. {r.Describe()}");
        }
    }

    private static async Task<NodeResult> RunAsync(string file, string[] args, string workingDirectory, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {file}");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var timedOut = false;
        using (var cts = new CancellationTokenSource(timeout))
        {
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // already exited
                }

                await proc.WaitForExitAsync();
            }
        }

        var cmd = file + " " + string.Join(" ", args);
        return new NodeResult(cmd, timedOut ? -1 : proc.ExitCode, await stdoutTask, await stderrTask, timedOut, timeout);
    }

    internal sealed record NodeResult(string Command, int ExitCode, string Stdout, string Stderr, bool TimedOut, TimeSpan Timeout)
    {
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append($"cmd: {Command}; exit={ExitCode}");
            if (TimedOut)
            {
                sb.Append($" (timed out after {Timeout.TotalSeconds:0}s)");
            }

            sb.Append("\n--- stdout ---\n").Append(Stdout.TrimEnd());
            sb.Append("\n--- stderr ---\n").Append(Stderr.TrimEnd());
            return sb.ToString();
        }
    }
}
