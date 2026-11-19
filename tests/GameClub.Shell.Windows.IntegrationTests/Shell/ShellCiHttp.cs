#if NET48
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GameClub.Shell.Windows.IntegrationTests.Shell;

internal static class ShellCiHttp
{
    private static string BaseUrl =>
        (Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL") ?? "http://127.0.0.1:5000").TrimEnd('/');

    private static string ApiKey =>
        Environment.GetEnvironmentVariable("GAMECLUB_API_KEY") ?? "ci-test-key";

    public static bool TryGetStationsOk()
    {
        try
        {
            var req = WebRequest.Create(BaseUrl + "/api/stations");
            req.Headers["X-Api-Key"] = ApiKey;
            req.Timeout = 5000;
            using var resp = (HttpWebResponse)req.GetResponse();
            return resp.StatusCode == HttpStatusCode.OK;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<OpenBillSnapshot> GetOpenSessionLiveBillAsync(Guid sessionId)
    {
        var cookie = await LoginOperatorAsync();
        var req = WebRequest.Create(BaseUrl + $"/api/operator/sessions/{sessionId}/bill/open");
        req.Headers["Cookie"] = cookie;
        req.Timeout = 10000;
        using var resp = (HttpWebResponse)await req.GetResponseAsync();
        using var stream = resp.GetResponseStream();
        using var reader = new StreamReader(stream!);
        var json = JsonDocument.Parse(reader.ReadToEnd());
        var root = json.RootElement;
        var amount = root.GetProperty("amountToman").GetInt64();
        var minutes = root.TryGetProperty("minutesPlayed", out var m) ? m.GetInt32() : (int?)null;
        return new OpenBillSnapshot(amount, minutes);
    }

    internal readonly record struct OpenBillSnapshot(long AmountToman, int? MinutesPlayed);

    public static async Task<Guid> CreateOpenSessionAsync(string stationId)
    {
        var client = WebRequest.Create(BaseUrl + "/api/stations");
        client.Method = "POST";
        client.ContentType = "application/json";
        client.Headers["X-Api-Key"] = ApiKey;
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = stationId, name = stationId, groupId = "pc-normal" }));
        client.ContentLength = body.Length;
        using (var s = client.GetRequestStream())
        {
            s.Write(body, 0, body.Length);
        }

        try
        {
            using var _ = (HttpWebResponse)client.GetResponse();
        }
        catch (WebException ex) when (ex.Response is HttpWebResponse { StatusCode: HttpStatusCode.Conflict })
        {
        }

        var startReq = WebRequest.Create(BaseUrl + "/api/sessions");
        startReq.Method = "POST";
        startReq.ContentType = "application/json";
        startReq.Headers["X-Api-Key"] = ApiKey;
        var startBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { stationId, playerCount = 1 }));
        startReq.ContentLength = startBody.Length;
        using (var s = startReq.GetRequestStream())
        {
            s.Write(startBody, 0, startBody.Length);
        }

        using var startResp = (HttpWebResponse)startReq.GetResponse();
        using var startStream = startResp.GetResponseStream();
        using var reader = new StreamReader(startStream!);
        var json = JsonDocument.Parse(reader.ReadToEnd());
        return json.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<string> LoginOperatorAsync()
    {
        var req = WebRequest.Create(BaseUrl + "/api/operator/auth/login");
        req.Method = "POST";
        req.ContentType = "application/json";
        var payload = Encoding.UTF8.GetBytes("{\"username\":\"operator\",\"pin\":\"1111\"}");
        req.ContentLength = payload.Length;
        using (var s = await req.GetRequestStreamAsync())
        {
            await s.WriteAsync(payload, 0, payload.Length);
        }

        using var resp = (HttpWebResponse)await req.GetResponseAsync();
        var setCookie = resp.Headers["Set-Cookie"];
        if (string.IsNullOrWhiteSpace(setCookie))
        {
            throw new InvalidOperationException("operator login missing cookie");
        }

        var semi = setCookie.IndexOf(';');
        return semi > 0 ? setCookie.Substring(0, semi) : setCookie;
    }
}
#endif