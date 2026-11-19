#if NET48
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GameClub.Kiosk.Windows.IntegrationTests.Server;
using Npgsql;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Wpf;

internal static class WpfLiveServerTestHelper
{
    public static int? LastTopUpStatus { get; private set; }

    public static int? LastCredentialStatus { get; private set; }

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
        using (var cmd = new NpgsqlCommand(
            """
            UPDATE rate_lines SET "HourlyToman" = 80000
            WHERE "GroupId" = 'pc-normal' AND "IsSpecialBand" = false AND "PlayMode" = 'Offline' AND "PlayerCount" = 1
            """,
            conn))
        {
            var rateRows = await cmd.ExecuteNonQueryAsync();
            if (rateRows != 1)
            {
                throw new InvalidOperationException(
                    $"Expected exactly one pc-normal offline rate line update (IsSpecialBand = false), but {rateRows} row(s) were affected.");
            }
        }

        using (var cmd = new NpgsqlCommand(
            """UPDATE branch_rounding SET "PcSpecialBandSurchargeToman" = 0""",
            conn))
        {
            var roundingRows = await cmd.ExecuteNonQueryAsync();
            if (roundingRows < 1)
            {
                throw new InvalidOperationException(
                    $"Expected at least one branch_rounding row update for PcSpecialBandSurchargeToman, but {roundingRows} row(s) were affected.");
            }
        }
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
        ProbeHttpCallTrace.Clear();
        StabStepLog.Write("setup_begin", "session_start_via=test_harness_no_session_start_kiosk_login_will_open_session");

        var baseUrl = RequireServerUrl();
        var apiKey = Environment.GetEnvironmentVariable("GAMECLUB_API_KEY") ?? "ci-test-key";
        var api = new HttpClient { BaseAddress = new Uri(baseUrl + "/") };
        api.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        await EnsurePcNormalRate80000Async();
        var stationId = "PC-WPF-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        StabStepLog.Write("station_created", $"rawStationId={stationId}");
        var registerBody = System.Text.Json.JsonSerializer.Serialize(new { id = stationId, name = stationId, groupId = "pc-normal" });
        var register = await TraceJsonAsync(api, HttpMethod.Post, "/api/stations", registerBody);
        register.EnsureSuccessStatusCode();

        var op = new HttpClient { BaseAddress = api.BaseAddress };
        var opLoginBody = System.Text.Json.JsonSerializer.Serialize(new { username = "manager", pin = "1234" });
        var opLogin = await TraceJsonAsync(op, HttpMethod.Post, "/api/operator/auth/login", opLoginBody);
        opLogin.EnsureSuccessStatusCode();
        var createdBody = System.Text.Json.JsonSerializer.Serialize(new { name = loginName, mobile = $"0912{new Random().Next(1_000_000, 9_999_999)}" });
        var created = await TraceJsonAsync(op, HttpMethod.Post, "/api/operator/customers", createdBody);
        created.EnsureSuccessStatusCode();
        var createdJson = await created.Content.ReadAsStringAsync();
        var customerId = ParseGuidProperty(createdJson, "id");
        var topUpBody = System.Text.Json.JsonSerializer.Serialize(new { method = "Cash", amountToman = 48_667L, commandId = Guid.NewGuid() });
        var topUp = await TraceJsonAsync(op, HttpMethod.Post, $"/api/operator/members/customers/{customerId}/wallet/top-up", topUpBody);
        LastTopUpStatus = (int)topUp.StatusCode;
        StabStepLog.Write("wallet_top_up", $"status={LastTopUpStatus}");
        topUp.EnsureSuccessStatusCode();
        var credBody = System.Text.Json.JsonSerializer.Serialize(new { loginName, password });
        var credential = await TraceJsonAsync(op, HttpMethod.Put, $"/api/operator/customers/{customerId}/credential", credBody);
        LastCredentialStatus = (int)credential.StatusCode;
        StabStepLog.Write("credential_put", $"status={LastCredentialStatus}");
        credential.EnsureSuccessStatusCode();

        var codeResponse = await TraceAsync(op, HttpMethod.Post, $"/api/operator/stations/{stationId}/pairing-code", null);
        await EnsureSuccessWithBodyAsync(codeResponse, $"POST /api/operator/stations/{stationId}/pairing-code");
        var codeJson = await codeResponse.Content.ReadAsStringAsync();
        var pairingCode = ParseStringProperty(codeJson, "code");
        var pairBody = System.Text.Json.JsonSerializer.Serialize(new { code = pairingCode });
        var pair = await TraceJsonAsync(api, HttpMethod.Post, "/api/kiosk/pair", pairBody);
        await EnsureSuccessWithBodyAsync(pair, "POST /api/kiosk/pair");
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
        StabStepLog.Write("setup_complete", $"stationId={stationId} deviceId={deviceId}");
        return stationId;
    }

    private static async Task<HttpResponseMessage> TraceJsonAsync(HttpClient client, HttpMethod method, string path, string jsonBody)
    {
        using var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(method, path) { Content = content };
        var response = await client.SendAsync(request);
        var resBody = await response.Content.ReadAsStringAsync();
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        response.Content = new StringContent(resBody, System.Text.Encoding.UTF8, mediaType);
        ProbeHttpCallTrace.Record(method.Method, path, (int)response.StatusCode, jsonBody, resBody);
        return response;
    }

    private static async Task<HttpResponseMessage> TraceAsync(HttpClient client, HttpMethod method, string path, HttpContent content)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        var response = await client.SendAsync(request);
        var resBody = await response.Content.ReadAsStringAsync();
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        response.Content = new StringContent(resBody, System.Text.Encoding.UTF8, mediaType);
        ProbeHttpCallTrace.Record(method.Method, path, (int)response.StatusCode, "", resBody);
        return response;
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

    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            $"{operation} failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}");
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
