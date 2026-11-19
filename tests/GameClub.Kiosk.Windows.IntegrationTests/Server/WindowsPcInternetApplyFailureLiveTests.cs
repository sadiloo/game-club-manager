#if NET48
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameClub.Billing;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Guard.Internet;
using GameClub.Kiosk.Windows.IntegrationTests;
using GameClub.Kiosk.Windows.IntegrationTests.Guard;
using Npgsql;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>L17 end-to-end — Guard apply fault (harness) surfaces on grid and segments at press.</summary>
[Trait("Category", "WindowsServer")]
public sealed class WindowsPcInternetApplyFailureLiveTests
{
    [Trait("Category", "LiveCheckpointServer")]
    [Fact(Timeout = 180_000)]
    public async Task live_server_pc_internet_guard_forced_apply_failure_shows_error_and_offline_at_press()
    {
        var serverUrl = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            IntegrationTestCi.FailIfActive("CHECKPOINT_SERVER_URL must be set in CI for live_server_pc_internet_guard_forced_apply_failure.");
            return;
        }

        LiveDatabaseResetInvoker.ResetViaHarnessTest();

        var apiKey = Environment.GetEnvironmentVariable("GAMECLUB_API_KEY") ?? "ci-test-key";
        KioskRuntime runtime = null;
        var pipeCts = new CancellationTokenSource();
        Thread pipe = null;
        try
        {
            var api = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/") };
            api.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
            var stationId = "PC-FAIL-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            (await api.PostAsJsonAsync("/api/stations", new { id = stationId, name = stationId, groupId = PilotSeed.PcNormalGroupId }))
                .EnsureSuccessStatusCode();

            var op = new HttpClient { BaseAddress = api.BaseAddress };
            (await op.PostAsJsonAsync("/api/operator/auth/login", new { username = "manager", pin = "1234" }))
                .EnsureSuccessStatusCode();
            var ratesJson = JsonSerializer.Serialize(new
            {
                onlineDay = PilotSeed.PcNormalHourly,
                onlineNight = PilotSeed.PcNormalSpecialHourly,
                offlineDay = 60_000L,
                offlineNight = 80_000L,
            });
            (await op.PutAsync(
                $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
                new StringContent(ratesJson, System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();

            var created = await op.PostAsJsonAsync(
                "/api/operator/customers",
                new { name = "failapply", mobile = $"0912{new Random().Next(1_000_000, 9_999_999)}" });
            created.EnsureSuccessStatusCode();
            var createdText = await created.Content.ReadAsStringAsync();
            Guid customerId;
            using (var createdDoc = JsonDocument.Parse(createdText))
            {
                customerId = createdDoc.RootElement.GetProperty("id").GetGuid();
            }

            await op.PostAsJsonAsync(
                $"/api/operator/members/customers/{customerId}/wallet/top-up",
                new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() });
            await op.PutAsync(
                $"/api/operator/customers/{customerId}/credential",
                new StringContent(
                    JsonSerializer.Serialize(new { loginName = "failapply", password = "pass" }),
                    System.Text.Encoding.UTF8,
                    "application/json"));

            var codeRsp = await op.PostAsync($"/api/operator/stations/{stationId}/pairing-code", null);
            codeRsp.EnsureSuccessStatusCode();
            var codeText = await codeRsp.Content.ReadAsStringAsync();
            string pairingCode;
            using (var codeDoc = JsonDocument.Parse(codeText))
            {
                pairingCode = codeDoc.RootElement.GetProperty("code").GetString();
            }

            var pair = await api.PostAsJsonAsync("/api/kiosk/pair", new { code = pairingCode });
            pair.EnsureSuccessStatusCode();
            var pairText = await pair.Content.ReadAsStringAsync();
            Guid deviceId;
            string deviceKey;
            string leasePem;
            using (var pairDoc = JsonDocument.Parse(pairText))
            {
                deviceId = pairDoc.RootElement.GetProperty("deviceId").GetGuid();
                deviceKey = pairDoc.RootElement.GetProperty("deviceKey").GetString();
                leasePem = pairDoc.RootElement.GetProperty("leaseVerifyPublicKeyPem").GetString();
            }

            var uri = new Uri(serverUrl);
            var config = new InternetGuardConfig
            {
                StationId = stationId,
                ServerHost = uri.Host,
                ServerPort = uri.Port > 0 ? uri.Port : 80,
                ServerIps = new[] { "127.0.0.1" },
                ServerPublicKeyPem = leasePem,
                ExtraAllowPrograms = Array.Empty<string>(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(InternetGuardPaths.ConfigPath));
            File.WriteAllText(InternetGuardPaths.ConfigPath, JsonSerializer.Serialize(config));
            var probeCts = new CancellationTokenSource();
            Thread probePipe = null;
            try
            {
                probePipe = new Thread(() => new InternetPipeServer(config).Run(probeCts.Token))
                {
                    IsBackground = true,
                    Name = "L17ProbeInternetPipe",
                };
                probePipe.Start();
                await Task.Delay(500);
                await L17RealInternetOffProbe.RunAsync(
                    config,
                    deviceId,
                    deviceKey,
                    new Uri(serverUrl.TrimEnd('/') + "/hubs/kiosk"),
                    stationId,
                    default);
            }
            finally
            {
                probeCts.Cancel();
                probePipe?.Join(5_000);
            }

            pipe = new Thread(() => new InternetPipeServer(config, new FaultInjectingInternetFirewallApplier()).Run(pipeCts.Token))
            {
                IsBackground = true,
                Name = "HarnessInternetPipe",
            };
            pipe.Start();
            await Task.Delay(500);

            var kioskHttp = new HttpClient { BaseAddress = api.BaseAddress };
            kioskHttp.DefaultRequestHeaders.Add("X-Device-Id", deviceId.ToString());
            kioskHttp.DefaultRequestHeaders.Add("X-Device-Key", deviceKey);
            var login = await kioskHttp.PostAsJsonAsync(
                "/api/kiosk/auth/login",
                new { loginName = "failapply", password = "pass", stationId });
            login.EnsureSuccessStatusCode();
            Guid sessionId;
            var loginText = await login.Content.ReadAsStringAsync();
            using (var loginDoc = JsonDocument.Parse(loginText))
            {
                sessionId = loginDoc.RootElement.GetProperty("sessionId").GetGuid();
            }

            var queuePath = Path.Combine(Path.GetTempPath(), $"kiosk-fail-{Guid.NewGuid():N}.db");
            runtime = new KioskRuntime(() => kioskHttp);
            runtime.Configure(new Uri(serverUrl.TrimEnd('/') + "/hubs/kiosk"), deviceId, deviceKey, stationId, queuePath, legacyWindows: true);
            runtime.BindSession(sessionId);
            await runtime.StartAsync(default);

            var rv = await WaitForStationRowVersionAsync(op, stationId, sessionId, TimeSpan.FromSeconds(60));
            var pressAt = DateTime.UtcNow;
            var togglePayload = new { on = false, expectedVersion = rv, commandId = Guid.NewGuid() };
            Console.WriteLine(
                $"LIVE_INTERNET_OFF_TOGGLE request stationId={stationId} sessionId={sessionId} body={JsonSerializer.Serialize(togglePayload)}");
            var toggle = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", togglePayload);
            var toggleBody = await toggle.Content.ReadAsStringAsync();
            Console.WriteLine(
                $"LIVE_INTERNET_OFF_TOGGLE response status={(int)toggle.StatusCode} body={toggleBody}");
            if (toggle.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                Console.WriteLine("LIVE_INTERNET_OFF_TOGGLE finding=409_conflict_after_login_complete rowVersion=" + rv);
            }

            WriteLiveInternetOffSnapshot(1, op, stationId, sessionId, runtime, queuePath);

            toggle.EnsureSuccessStatusCode();

            string internetError = null;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                var gridText = await op.GetStringAsync("/api/operator/grid");
                using var grid = JsonDocument.Parse(gridText);
                foreach (var tile in grid.RootElement.EnumerateArray())
                {
                    if (tile.GetProperty("id").GetString() != stationId)
                    {
                        continue;
                    }

                    if (tile.TryGetProperty("internetError", out var errProp))
                    {
                        internetError = errProp.GetString();
                        if (!string.IsNullOrWhiteSpace(internetError))
                        {
                            break;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(internetError))
                {
                    break;
                }

                await Task.Delay(1_000);
            }

            WriteLiveInternetOffSnapshot(2, op, stationId, sessionId, runtime, queuePath);

            Assert.Contains("apply_failed", internetError ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            using var conn = new NpgsqlConnection(
                Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
                ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE"));
            await conn.OpenAsync();
            using var cmd = new NpgsqlCommand(
                """
                SELECT "Mode", "StartUtc" FROM play_segments
                WHERE "SessionId" = @id ORDER BY "StartUtc"
                """,
                conn);
            cmd.Parameters.AddWithValue("id", sessionId);
            var modes = new List<string>();
            var starts = new List<DateTime>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                modes.Add(reader.GetString(0));
                starts.Add(reader.GetDateTime(1));
            }

            Assert.Equal("Offline", modes[modes.Count - 1]);
            Assert.True(starts[modes.Count - 1].ToUniversalTime() >= pressAt.AddSeconds(-8), "Offline segment must start at toggle press.");
            Console.WriteLine(
                $"L17_CI internetError={internetError} offlineAtPressUtc={starts[starts.Count - 1].ToUniversalTime():o} pressAtUtc={pressAt:o}");
        }
        finally
        {
            pipeCts.Cancel();
            pipe?.Join(5_000);
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }
        }
    }

    private static async Task<uint> WaitForStationRowVersionAsync(
        HttpClient op,
        string stationId,
        Guid sessionId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var gridText = await op.GetStringAsync("/api/operator/grid");
            using var grid = JsonDocument.Parse(gridText);
            foreach (var tile in grid.RootElement.EnumerateArray())
            {
                if (tile.GetProperty("id").GetString() != stationId)
                {
                    continue;
                }

                if (!tile.TryGetProperty("sessionId", out var sid) || sid.GetGuid() != sessionId)
                {
                    continue;
                }

                if (tile.TryGetProperty("rowVersion", out var rvProp))
                {
                    return rvProp.GetUInt32();
                }
            }

            await Task.Delay(250);
        }

        throw new InvalidOperationException($"station {stationId} with session {sessionId} missing from operator grid");
    }

    private static void WriteLiveInternetOffSnapshot(
        int snap,
        HttpClient op,
        string stationId,
        Guid sessionId,
        KioskRuntime runtime,
        string queuePath)
    {
        try
        {
            var segmentLine = "segments=unavailable";
            var queueLine = "kioskQueuePending=unknown";
            var serverRow = "internetState=unavailable";

            using (var conn = new NpgsqlConnection(
                       Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
                       ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")))
            {
                conn.Open();
                using (var seg = new NpgsqlCommand(
                           """SELECT "Mode", "StartUtc" FROM play_segments WHERE "SessionId" = @id ORDER BY "StartUtc" """,
                           conn))
                {
                    seg.Parameters.AddWithValue("id", sessionId);
                    var parts = new List<string>();
                    using var reader = seg.ExecuteReader();
                    while (reader.Read())
                    {
                        parts.Add($"{reader.GetString(0)}@{reader.GetDateTime(1):o}");
                    }

                    segmentLine = "segments=" + string.Join("|", parts);
                }

                using (var inet = new NpgsqlCommand(
                           """
                           SELECT "DesiredOn", "DesiredVersion", "AppliedOn", "AppliedVersion", "LastError"
                           FROM station_internet_state WHERE "StationId" = @id
                           """,
                           conn))
                {
                    inet.Parameters.AddWithValue("id", stationId);
                    using var reader = inet.ExecuteReader();
                    if (reader.Read())
                    {
                        serverRow =
                            $"internetState desiredOn={reader.GetBoolean(0)} desiredVer={reader.GetInt64(1)} "
                            + $"appliedOn={(reader.IsDBNull(2) ? "null" : reader.GetBoolean(2).ToString())} "
                            + $"appliedVer={(reader.IsDBNull(3) ? "null" : reader.GetInt64(3).ToString())} "
                            + $"lastError={(reader.IsDBNull(4) ? "" : reader.GetString(4))}";
                    }
                }
            }

            if (runtime is not null)
            {
                var pending = runtime.GetQueue().PendingAsync().GetAwaiter().GetResult().Count;
                queueLine = $"kioskQueuePending={pending}";
            }

            var guardProcs = Process.GetProcessesByName("GameClub.Kiosk.Guard");
            var guardSvc = Process.GetProcessesByName("CheckpointGuard");
            var guardRunning = guardProcs.Length + guardSvc.Length;
            var pipeAlive = Process.GetProcessesByName("testhost").Length;

            var statePresent = File.Exists(InternetGuardPaths.StatePath);
            var statePayload = statePresent ? File.ReadAllText(InternetGuardPaths.StatePath) : "";
            var guardLogTail = ReadTail(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Checkpoint", "guard", "watchdog.log"), 8);
            var ruleCount = ReadGuardRuleCount();
            var egress = ProbeEgress("1.1.1.1", 443);

            Console.WriteLine(
                $"LIVE_INTERNET_OFF_SNAPSHOT snap={snap} stationId={stationId} sessionId={sessionId} "
                + $"{segmentLine} {serverRow} {queueLine} harnessPipeRunning={pipeAlive > 0} "
                + $"guardProcesses={guardRunning} guardRuleCount={ruleCount} internetStatePath={InternetGuardPaths.StatePath} "
                + $"internetStatePresent={statePresent} internetStatePayload={statePayload} guardLogTail={guardLogTail} "
                + $"egressProbe_1.1.1.1_443_blocked={egress}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"LIVE_INTERNET_OFF_SNAPSHOT snap={snap} stationId={stationId} error={ex.Message}");
        }
    }

    private static string ReadTail(string path, int maxLines)
    {
        if (!File.Exists(path))
        {
            return "";
        }

        var lines = File.ReadAllLines(path);
        return string.Join(" | ", lines.Skip(Math.Max(0, lines.Length - maxLines)));
    }

    private static int ReadGuardRuleCount()
    {
        try
        {
            var script =
                "(Get-NetFirewallRule -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count";
            var psi = new ProcessStartInfo("powershell", "-NoProfile -Command " + script)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10_000);
            return int.TryParse(text, out var count) ? count : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static bool ProbeEgress(string host, int port)
    {
        try
        {
            using var tcp = new TcpClient();
            var task = tcp.ConnectAsync(host, port);
            return !(task.Wait(2_000) && tcp.Connected);
        }
        catch
        {
            return true;
        }
    }
}
#endif