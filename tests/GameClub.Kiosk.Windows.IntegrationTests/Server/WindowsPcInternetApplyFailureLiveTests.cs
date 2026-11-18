#if NET48
#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameClub.Billing;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Guard.Internet;
using Npgsql;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>L17 end-to-end — Guard apply fault (harness) surfaces on grid and segments at press.</summary>
[Trait("Category", "WindowsServer")]
public sealed class WindowsPcInternetApplyFailureLiveTests
{
    [Fact(Timeout = 180_000)]
    public async Task live_server_pc_internet_guard_forced_apply_failure_shows_error_and_offline_at_press()
    {
        var serverUrl = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return;
        }

        var apiKey = Environment.GetEnvironmentVariable("GAMECLUB_API_KEY") ?? "ci-test-key";
        var priorFault = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_APPLY_FAULT");
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_APPLY_FAULT", "1");
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
            pipe = new Thread(() => new InternetPipeServer(config).Run(pipeCts.Token))
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
            await Task.Delay(2_000);

            var gridText = await op.GetStringAsync("/api/operator/grid");
            using var grid0 = JsonDocument.Parse(gridText);
            uint rv = 0;
            foreach (var tile in grid0.RootElement.EnumerateArray())
            {
                if (tile.GetProperty("id").GetString() == stationId)
                {
                    rv = tile.GetProperty("rowVersion").GetUInt32();
                    break;
                }
            }

            var pressAt = DateTime.UtcNow;
            (await op.PostAsJsonAsync(
                $"/api/operator/sessions/{sessionId}/internet",
                new { on = false, expectedVersion = rv, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();

            string internetError = null;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                gridText = await op.GetStringAsync("/api/operator/grid");
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
            Assert.True(starts[starts.Count - 1].ToUniversalTime() >= pressAt.AddSeconds(-8), "Offline segment must start at toggle press.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_APPLY_FAULT", priorFault);
            pipeCts.Cancel();
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }
        }
    }
}
#endif