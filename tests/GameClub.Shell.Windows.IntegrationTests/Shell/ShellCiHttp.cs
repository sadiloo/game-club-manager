#if NET48
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace GameClub.Shell.Windows.IntegrationTests.Shell;

internal static class ShellCiHttp
{
    private static readonly Dictionary<Guid, DateTime> FirstSegmentStartUtcBySession = new();

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

    internal sealed record OpenSessionHandle(Guid SessionId, DateTime FirstSegmentStartUtc);

    public static async Task<OpenSessionHandle> CreateOpenSessionAsync(string stationId)
    {
        var client = WebRequest.Create(BaseUrl + "/api/stations");
        client.Method = "POST";
        client.ContentType = "application/json";
        client.Headers["X-Api-Key"] = ApiKey;
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id = stationId, name = stationId, groupId = "ps5" }));
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

        Guid sessionId;
        using (var startResp = (HttpWebResponse)startReq.GetResponse())
        {
            using var startStream = startResp.GetResponseStream();
            using var reader = new StreamReader(startStream!);
            var json = JsonDocument.Parse(reader.ReadToEnd());
            sessionId = json.RootElement.GetProperty("id").GetGuid();
        }

        var cookie = await LoginOperatorAsync();
        var (_, serverNow) = await GetOperatorJsonAsync($"/api/operator/sessions/{sessionId}/close-settle-preview", cookie);
        var startUtc = serverNow ?? DateTime.UtcNow;
        FirstSegmentStartUtcBySession[sessionId] = startUtc;
        return new OpenSessionHandle(sessionId, startUtc);
    }

    public static async Task<string> GetOperatorGridStatusForStationAsync(string stationId)
    {
        var cookie = await LoginOperatorAsync();
        var (grid, _) = await GetOperatorJsonAsync("/api/operator/grid", cookie);
        foreach (var tile in grid.RootElement.EnumerateArray())
        {
            if (!tile.TryGetProperty("id", out var idEl))
            {
                continue;
            }

            if (!string.Equals(idEl.GetString(), stationId, StringComparison.Ordinal))
            {
                continue;
            }

            return tile.GetProperty("status").GetString()
                ?? throw new InvalidOperationException($"grid tile for {stationId} missing status");
        }

        throw new InvalidOperationException($"station {stationId} not found on operator grid");
    }

    public static async Task<DateTime> GetFirstSegmentStartUtcFromServerAsync(Guid sessionId)
    {
        var cookie = await LoginOperatorAsync();
        var (preview, _) = await GetOperatorJsonAsync($"/api/operator/sessions/{sessionId}/close-settle-preview", cookie);
        if (preview.RootElement.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array && segments.GetArrayLength() > 0)
        {
            return ParseFirstSegmentStartUtc(preview.RootElement);
        }

        if (!FirstSegmentStartUtcBySession.TryGetValue(sessionId, out var startUtc))
        {
            throw new InvalidOperationException($"no cached segment startUtc for session {sessionId}");
        }

        return startUtc;
    }

    public static async Task<FrozenBillDetail> OpenFrozenBillAsync(Guid sessionId)
    {
        var cookie = await LoginOperatorAsync();
        await PostOperatorAsync($"/api/operator/sessions/{sessionId}/bill/open", cookie);
        var (root, _) = await GetOperatorJsonAsync($"/api/operator/sessions/{sessionId}/bill", cookie);
        return ParseFrozenBillDetail(root.RootElement);
    }

    private static DateTime ParseFirstSegmentStartUtc(JsonElement billRoot)
    {
        var segments = billRoot.GetProperty("segments");
        if (segments.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("bill response has no segments");
        }

        var first = segments.EnumerateArray()
            .OrderBy(s => ParseUtcProperty(s.GetProperty("startUtc")))
            .First();
        return ParseUtcProperty(first.GetProperty("startUtc"));
    }

    internal sealed record FrozenBillDetail(
        long CollectibleToman,
        long TimeGrossToman,
        int SegmentCount,
        DateTime FrozenAtUtc,
        DateTime FirstSegmentStartUtc,
        IReadOnlyList<FrozenBillSegment> Segments);

    internal sealed record FrozenBillSegment(string Mode, int PlayerCount, long HourlyToman, string SegmentKey);

    private static FrozenBillDetail ParseFrozenBillDetail(JsonElement root)
    {
        if (!root.TryGetProperty("collectibleToman", out var collectible))
        {
            throw new InvalidOperationException("bill response missing collectibleToman");
        }

        if (!root.TryGetProperty("timeGrossToman", out var timeGross))
        {
            throw new InvalidOperationException("bill response missing timeGrossToman");
        }

        if (!root.TryGetProperty("frozenAtUtc", out var frozenAt))
        {
            throw new InvalidOperationException("bill response missing frozenAtUtc");
        }

        var segmentsEl = root.GetProperty("segments");
        var segmentDtos = new List<FrozenBillSegment>();
        foreach (var seg in segmentsEl.EnumerateArray())
        {
            if (!seg.TryGetProperty("mode", out var mode))
            {
                throw new InvalidOperationException("bill segment missing mode");
            }

            if (!seg.TryGetProperty("playerCount", out var players))
            {
                throw new InvalidOperationException("bill segment missing playerCount");
            }

            if (!seg.TryGetProperty("hourlyToman", out var hourly))
            {
                throw new InvalidOperationException("bill segment missing hourlyToman");
            }

            var key = seg.TryGetProperty("segmentKey", out var sk) ? sk.GetString() ?? "" : "";
            segmentDtos.Add(new FrozenBillSegment(mode.GetString() ?? "", players.GetInt32(), hourly.GetInt64(), key));
        }

        var firstStart = ParseFirstSegmentStartUtc(root);
        return new FrozenBillDetail(
            collectible.GetInt64(),
            timeGross.GetInt64(),
            segmentDtos.Count,
            ParseUtcProperty(frozenAt),
            firstStart,
            segmentDtos);
    }

    private static DateTime ParseUtcProperty(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? DateTime.Parse(value.GetString()!, null, DateTimeStyles.RoundtripKind)
            : value.GetDateTime();

    public static void WaitUntilBothSessionsClearMinuteBoundary(
        DateTime refStartUtc,
        DateTime shellStartUtc,
        TimeSpan maxWait,
        double marginSeconds = 15)
    {
        var deadline = DateTime.UtcNow.Add(maxWait);
        while (DateTime.UtcNow < deadline)
        {
            var now = DateTime.UtcNow;
            if (IsAtLeastMarginFromMinuteBoundary(refStartUtc, now, marginSeconds)
                && IsAtLeastMarginFromMinuteBoundary(shellStartUtc, now, marginSeconds))
            {
                Console.WriteLine(
                    $"SHELL-MONEY minute-boundary-clear UTC={now:O} refStart={refStartUtc:O} shellStart={shellStartUtc:O}");
                return;
            }

            Thread.Sleep(200);
        }

        throw new InvalidOperationException(
            $"Sessions did not reach {marginSeconds:0}s margin from a whole-minute boundary within {maxWait.TotalSeconds:0}s "
            + $"(refStart={refStartUtc:O}, shellStart={shellStartUtc:O}, now={DateTime.UtcNow:O}).");
    }

    private static bool IsAtLeastMarginFromMinuteBoundary(DateTime segmentStartUtc, DateTime nowUtc, double marginSeconds)
    {
        var elapsed = (nowUtc - segmentStartUtc).TotalSeconds;
        if (elapsed < 0)
        {
            return false;
        }

        var mod = elapsed % 60;
        var distToPrev = mod;
        var distToNext = mod == 0 ? 60 : 60 - mod;
        var margin = Math.Min(distToPrev, distToNext);
        return margin >= marginSeconds;
    }

    public static int WholeElapsedMinutes(DateTime segmentStartUtc, DateTime frozenAtUtc)
        => (int)((frozenAtUtc - segmentStartUtc).TotalMinutes);

    public static void LogShellMoneyLine(string label, FrozenBillDetail bill)
    {
        var elapsed = WholeElapsedMinutes(bill.FirstSegmentStartUtc, bill.FrozenAtUtc);
        Console.WriteLine(
            $"SHELL-MONEY session={label} startUtc={bill.FirstSegmentStartUtc:O} frozenAtUtc={bill.FrozenAtUtc:O} "
            + $"elapsedWholeMin={elapsed} collectibleToman={bill.CollectibleToman} timeGrossToman={bill.TimeGrossToman}");
    }

    private static async Task<(JsonDocument Document, DateTime? ServerDateUtc)> GetOperatorJsonAsync(string path, string cookie)
    {
        var req = WebRequest.Create(BaseUrl + path);
        req.Method = "GET";
        req.Headers["Cookie"] = cookie;
        req.Timeout = 15000;
        try
        {
            using var resp = (HttpWebResponse)await req.GetResponseAsync();
            using var stream = resp.GetResponseStream();
            using var reader = new StreamReader(stream!);
            var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
            var serverDate = TryParseHttpDateUtc(resp.Headers["Date"]);
            return (doc, serverDate);
        }
        catch (WebException ex) when (ex.Response is HttpWebResponse error)
        {
            using var stream = error.GetResponseStream();
            using var reader = stream is null ? null : new StreamReader(stream);
            var body = reader?.ReadToEnd() ?? "";
            throw new InvalidOperationException(
                $"GET {path} failed: {(int)error.StatusCode} {error.StatusDescription}; body={body}",
                ex);
        }
    }

    private static DateTime? TryParseHttpDateUtc(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        if (DateTime.TryParseExact(
                header,
                "r",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static async Task PostOperatorAsync(string path, string cookie)
    {
        var req = WebRequest.Create(BaseUrl + path);
        req.Method = "POST";
        req.Headers["Cookie"] = cookie;
        req.ContentLength = 0;
        try
        {
            using var _ = (HttpWebResponse)await req.GetResponseAsync();
        }
        catch (WebException ex) when (ex.Response is HttpWebResponse error)
        {
            using var stream = error.GetResponseStream();
            using var reader = stream is null ? null : new StreamReader(stream);
            var body = reader?.ReadToEnd() ?? "";
            throw new InvalidOperationException(
                $"POST {path} failed: {(int)error.StatusCode} {error.StatusDescription}; body={body}",
                ex);
        }
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