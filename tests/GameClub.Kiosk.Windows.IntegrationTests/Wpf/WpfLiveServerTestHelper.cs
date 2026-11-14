#if NET48
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Wpf;

internal static class WpfLiveServerTestHelper
{
    public static string RequireServerUrl()
    {
        var url = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL")?.Trim();
        Assert.False(string.IsNullOrWhiteSpace(url), "CHECKPOINT_SERVER_URL must be set for live WPF remaining-time proof.");
        return url!.TrimEnd('/');
    }

    public static async Task EnsurePcNormalRate80000Async()
    {
        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        using var cmd = new NpgsqlCommand(
            """
            UPDATE rate_lines SET "HourlyToman" = 80000
            WHERE "GroupId" = 'pc-normal' AND "IsSpecialBand" = false AND "PlayMode" = 'Offline' AND "PlayerCount" = 1
            """,
            conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<Guid> LookupCustomerIdByLoginAsync(string loginName)
    {
        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        using var cmd = new NpgsqlCommand(
            """
            SELECT c."CustomerId"
            FROM customer_credentials c
            WHERE c."LoginName" = @login
            """,
            conn);
        cmd.Parameters.AddWithValue("login", loginName);
        return (Guid)(await cmd.ExecuteScalarAsync() ?? throw new InvalidOperationException($"No customer for login {loginName}."));
    }

    public static async Task<long> ReadWalletTomanAsync(Guid customerId)
    {
        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        using var cmd = new NpgsqlCommand(
            """SELECT COALESCE("RealToman",0) FROM customer_members WHERE "CustomerId" = @id""",
            conn);
        cmd.Parameters.AddWithValue("id", customerId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    public static Guid LookupCustomerIdByLogin(string loginName)
    {
        using var conn = new NpgsqlConnection(ResolveConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            """
            SELECT c."CustomerId"
            FROM customer_credentials c
            WHERE c."LoginName" = @login
            """,
            conn);
        cmd.Parameters.AddWithValue("login", loginName);
        return (Guid)(cmd.ExecuteScalar() ?? throw new InvalidOperationException($"No customer for login {loginName}."));
    }

    public static long ReadWalletToman(Guid customerId)
    {
        using var conn = new NpgsqlConnection(ResolveConnectionString());
        conn.Open();
        using var cmd = new NpgsqlCommand(
            """SELECT COALESCE("RealToman",0) FROM customer_members WHERE "CustomerId" = @id""",
            conn);
        cmd.Parameters.AddWithValue("id", customerId);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    public static async Task<string> SetupFundedSessionAsync(string loginName, string password)
    {
        var baseUrl = RequireServerUrl();
        var apiKey = Environment.GetEnvironmentVariable("GAMECLUB_API_KEY") ?? "ci-test-key";
        var api = new HttpClient { BaseAddress = new Uri(baseUrl + "/") };
        api.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        await EnsurePcNormalRate80000Async();
        var stationId = "PC-WPF-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var register = await api.PostAsJsonAsync("/api/stations", new { id = stationId, name = stationId, groupId = "pc-normal" });
        register.EnsureSuccessStatusCode();

        var op = new HttpClient { BaseAddress = api.BaseAddress };
        var opLogin = await op.PostAsJsonAsync("/api/operator/auth/login", new { username = "manager", pin = "1234" });
        opLogin.EnsureSuccessStatusCode();
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = loginName, mobile = $"0912{new Random().Next(1_000_000, 9_999_999)}" });
        created.EnsureSuccessStatusCode();
        var createdJson = await created.Content.ReadAsStringAsync();
        var customerId = ParseGuidProperty(createdJson, "id");
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new { method = "Cash", amountToman = 48_667L });
        await op.PutJsonAsync($"/api/operator/customers/{customerId}/credential", new { loginName, password });

        var codeResponse = await op.PostAsync($"/api/operator/stations/{stationId}/pairing-code", null);
        codeResponse.EnsureSuccessStatusCode();
        var codeJson = await codeResponse.Content.ReadAsStringAsync();
        var pairingCode = ParseStringProperty(codeJson, "code");
        var pair = await api.PostAsJsonAsync("/api/kiosk/pair", new { code = pairingCode });
        pair.EnsureSuccessStatusCode();
        var pairJson = await pair.Content.ReadAsStringAsync();
        var deviceId = ParseGuidProperty(pairJson, "deviceId");
        var deviceKey = ParseStringProperty(pairJson, "deviceKey");
        var leasePem = ParseStringProperty(pairJson, "leaseVerifyPublicKeyPem");
        if (string.IsNullOrWhiteSpace(leasePem) ||
            !leasePem.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "pair response leaseVerifyPublicKeyPem is not valid PEM after JSON parse.");
        }

        WriteDeviceJson(baseUrl, stationId, deviceId, deviceKey, leasePem);
        return stationId;
    }

    public static void WriteDeviceJson(string serverBase, string stationId, Guid deviceId, string deviceKey, string leasePem)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Checkpoint", "kiosk");
        Directory.CreateDirectory(root);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            deviceId,
            deviceKey,
            stationId,
            serverBase,
            leaseVerifyPublicKeyPem = leasePem,
        });
        File.WriteAllText(Path.Combine(root, "device.json"), json);
    }

    private static string ResolveConnectionString() =>
        Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
        ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")
        ?? throw new InvalidOperationException("GAMECLUB_DATABASE is not set.");

    private static Guid ParseGuidProperty(string json, string prop)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(prop).GetGuid();
    }

    private static string ParseStringProperty(string json, string prop)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(prop).GetString()!;
    }
}

internal static class WpfHttpJson
{
    public static Task<HttpResponseMessage> PostAsJsonAsync(this HttpClient client, string url, object body)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        return client.PostAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
    }

    public static Task<HttpResponseMessage> PutJsonAsync(this HttpClient client, string url, object body)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        return client.PutAsync(url, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
    }
}
#endif
