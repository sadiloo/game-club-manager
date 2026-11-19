#if NET48
#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

internal static class ProbeHttpCallTrace
{
    private static readonly object Gate = new();
    private static readonly List<string> Lines = new();

    private static readonly Regex SensitiveJson = new(
        "\"(password|pin|deviceKey|token|newPassword)\"\\s*:\\s*\"[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static void Record(string method, string path, int status, string requestBody, string responseBody)
    {
        var utc = DateTime.UtcNow;
        var ms = (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        var req = Redact(requestBody);
        var res = Redact(responseBody);
        if (req.Length > 2048)
        {
            req = req.Substring(0, 2048) + "...(truncated)";
        }

        if (res.Length > 2048)
        {
            res = res.Substring(0, 2048) + "...(truncated)";
        }

        var line =
            "PROBE_HTTP_TRACE "
            + $"utc={utc.ToString("O", CultureInfo.InvariantCulture)} utc_ms={ms} "
            + $"method={method} path={path} status={status} "
            + $"req_body={req} res_body={res}";
        lock (Gate)
        {
            Lines.Add(line);
        }

        Console.WriteLine(line);
        Console.Out.Flush();
    }

    internal static IReadOnlyList<string> Snapshot()
    {
        lock (Gate)
        {
            return Lines.ToArray();
        }
    }

    internal static void Clear()
    {
        lock (Gate)
        {
            Lines.Clear();
        }
    }

    internal static string Redact(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return "";
        }

        return SensitiveJson.Replace(body, m => $"\"{m.Groups[1].Value}\":\"[redacted]\"");
    }
}
#endif