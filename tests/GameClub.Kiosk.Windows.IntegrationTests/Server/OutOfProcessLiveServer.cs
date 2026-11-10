#if NET10_0_OR_GREATER
using System.Diagnostics;
using System.Net.Http.Headers;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>Supervise a live GameClub.Server process for transport-outage tests (CI subprocess or windows-2022 job).</summary>
internal sealed class OutOfProcessLiveServer : IAsyncDisposable
{
    private Process? _process;
    private readonly string _repoRoot;
    private readonly string _serverDll;

    private OutOfProcessLiveServer(string repoRoot, string serverDll, Process process)
    {
        _repoRoot = repoRoot;
        _serverDll = serverDll;
        _process = process;
    }

    public string BaseUrl { get; private set; } = "";

    public int? ProcessId => _process?.Id;

    public static OutOfProcessLiveServer AttachExisting(int processId, string baseUrl)
    {
        var proc = Process.GetProcessById(processId);
        var dll = Environment.GetEnvironmentVariable("GAMECLUB_SERVER_DLL") ?? "";
        var root = ResolveRepoRoot();
        return new OutOfProcessLiveServer(root, dll, proc) { BaseUrl = baseUrl.TrimEnd('/') };
    }

    public static async Task<OutOfProcessLiveServer> StartNewAsync(CancellationToken ct = default)
    {
        var root = ResolveRepoRoot();
        var dll = ResolveServerDll(root);
        var baseUrl = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL")?.TrimEnd('/')
            ?? "http://127.0.0.1:5225";
        var instance = new OutOfProcessLiveServer(root, dll, null!) { BaseUrl = baseUrl };
        await instance.LaunchAndWaitReadyAsync(ct);
        return instance;
    }

    public bool IsOwnedProcess(int pid) => _process is not null && _process.Id == pid;

    public void Stop()
    {
        if (_process is null || _process.HasExited)
        {
            return;
        }

        try
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(15_000);
        }
        catch
        {
            // process may already be gone
        }
    }

    public async Task RelaunchAsync(CancellationToken ct = default)
    {
        Stop();
        _process = null;
        await LaunchAndWaitReadyAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null && !_process.HasExited && Environment.GetEnvironmentVariable("GAMECLUB_SERVER_PID") is null)
        {
            Stop();
        }

        _process?.Dispose();
        await Task.CompletedTask;
    }

    private async Task LaunchAndWaitReadyAsync(CancellationToken ct)
    {
        var outLog = Path.Combine(Path.GetTempPath(), $"gcm-server-out-{Guid.NewGuid():N}.log");
        var errLog = Path.Combine(Path.GetTempPath(), $"gcm-server-err-{Guid.NewGuid():N}.log");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{_serverDll}\"",
            WorkingDirectory = _repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        CopyServerEnvironment(psi);
        psi.Environment["ASPNETCORE_URLS"] = BaseUrl;
        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start GameClub.Server.");
        _ = Task.Run(() => File.AppendAllText(outLog, _process.StandardOutput.ReadToEnd()));
        _ = Task.Run(() => File.AppendAllText(errLog, _process.StandardError.ReadToEnd()));

        var apiKey = Environment.GetEnvironmentVariable("GAMECLUB_API_KEY") ?? "ci-test-key";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        for (var i = 0; i < 60; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var response = await http.GetAsync($"{BaseUrl}/api/stations", ct);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // retry
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        throw new InvalidOperationException($"GameClub.Server did not become ready at {BaseUrl} (dll={_serverDll}).");
    }

    private static void CopyServerEnvironment(ProcessStartInfo psi)
    {
        foreach (var key in new[] { "GAMECLUB_DATABASE", "GAMECLUB_TEST_DATABASE", "GAMECLUB_API_KEY", "OperatorSessionSecret", "ASPNETCORE_ENVIRONMENT" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                psi.Environment[key] = value;
            }
        }

        if (!psi.Environment.ContainsKey("ASPNETCORE_ENVIRONMENT")
            || string.IsNullOrWhiteSpace(psi.Environment["ASPNETCORE_ENVIRONMENT"]))
        {
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
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

        throw new InvalidOperationException("Could not locate repository root for GameClub.Server.");
    }

    private static string ResolveServerDll(string repoRoot)
    {
        var fromEnv = Environment.GetEnvironmentVariable("GAMECLUB_SERVER_DLL");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var candidates = new[]
        {
            Path.Combine(repoRoot, "src/GameClub.Server/bin/Release/net10.0/GameClub.Server.dll"),
            Path.Combine(repoRoot, "src/GameClub.Server/bin/Debug/net10.0/GameClub.Server.dll"),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new InvalidOperationException("GameClub.Server.dll not found; build the server before running live outage tests.");
    }
}
#endif
