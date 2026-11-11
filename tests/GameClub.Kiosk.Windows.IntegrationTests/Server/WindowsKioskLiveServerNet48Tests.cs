#if NET48
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GameClub.Kiosk.Core.Runtime;
using Npgsql;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>net48 kiosk runtime against the live out-of-process server (windows-2022 proof).</summary>
[Trait("Category", "WindowsServer")]
public sealed class WindowsKioskLiveServerNet48Tests
{
    [Fact(Timeout = 180_000)]
    public async Task live_server_net48_transport_heartbeat_rows()
    {
        var baseUrl = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL")?.Trim();
        Assert.False(string.IsNullOrWhiteSpace(baseUrl), "CHECKPOINT_SERVER_URL must be set on windows-2022 kiosk integration.");

        var apiKey = Environment.GetEnvironmentVariable("GAMECLUB_API_KEY") ?? "ci-test-key";
        var api = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        api.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        var stationId = "PC-N48-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        await RegisterPcAsync(api, stationId);
        var (deviceId, deviceKey) = await PairAsync(api, stationId);
        var kioskHttp = CreateKioskClient(baseUrl, deviceId, deviceKey);

        var heartbeatBefore = await CountHeartbeatsAsync();
        var queuePath = Path.Combine(Path.GetTempPath(), $"kiosk-n48-{Guid.NewGuid():N}.db");
        var runtime = new KioskRuntime(() => kioskHttp);
        runtime.Configure(new Uri(baseUrl.TrimEnd('/') + "/hubs/kiosk"), deviceId, deviceKey, stationId, queuePath, legacyWindows: true);
        await runtime.StartAsync(default);

        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            if (await CountHeartbeatsAsync() >= heartbeatBefore + 3)
            {
                await runtime.DisposeAsync();
                return;
            }
        }

        await runtime.DisposeAsync();
        Assert.Fail("Expected at least 3 server-side heartbeat rows from net48 KioskRuntime.");
    }

    private static async Task RegisterPcAsync(HttpClient api, string stationId)
    {
        var response = await api.PostAsJsonAsync("/api/stations", new { id = stationId, name = stationId, groupId = "pc-normal" });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<(Guid DeviceId, string DeviceKey)> PairAsync(HttpClient api, string stationId)
    {
        var opLogin = new HttpClient { BaseAddress = api.BaseAddress };
        var login = await opLogin.PostAsJsonAsync("/api/operator/auth/login", new { username = "manager", pin = "1234" });
        login.EnsureSuccessStatusCode();
        var code = await opLogin.PostAsync($"/api/operator/stations/{stationId}/pairing-code", null);
        code.EnsureSuccessStatusCode();
        var codeText = await code.Content.ReadAsStringAsync();
        string pairingCode;
        using (var codeDoc = JsonDocument.Parse(codeText))
        {
            pairingCode = codeDoc.RootElement.GetProperty("code").GetString()!;
        }

        var pair = await api.PostAsJsonAsync("/api/kiosk/pair", new { code = pairingCode });
        pair.EnsureSuccessStatusCode();
        var body = await pair.Content.ReadAsStringAsync();
        using var pairDoc = JsonDocument.Parse(body);
        var deviceId = pairDoc.RootElement.GetProperty("deviceId").GetGuid();
        var deviceKey = pairDoc.RootElement.GetProperty("deviceKey").GetString()!;
        return (deviceId, deviceKey);
    }

    private static HttpClient CreateKioskClient(string baseUrl, Guid deviceId, string deviceKey)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        client.DefaultRequestHeaders.Add("X-Device-Id", deviceId.ToString());
        client.DefaultRequestHeaders.Add("X-Device-Key", deviceKey);
        return client;
    }

    private static async Task<int> CountHeartbeatsAsync()
    {
        var cs = Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
            ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE");
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        using (var cmd = new NpgsqlCommand("SELECT COUNT(*)::int FROM kiosk_heartbeats", conn))
        {
            return (int)(await cmd.ExecuteScalarAsync() ?? 0);
        }
    }
}

internal static class Net48JsonHttpExtensions
{
    public static Task<HttpResponseMessage> PostAsJsonAsync(this HttpClient client, string url, object body)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        return client.PostAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
    }
}
#endif
