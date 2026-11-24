using System;
using System.IO;
using System.Net;
using System.Text.Json;

namespace GameClub.Shell.Shell;

public sealed class LocalServerStatusDto
{
    public DateTimeOffset? StartedAtUtc { get; set; }
    public bool DbOk { get; set; }
}

internal static class LocalServerStatusClient
{
    private const string Url = "http://127.0.0.1:5000/api/local/server-status";

    public static bool TryRead(out LocalServerStatusDto? dto)
    {
        dto = null;
        if (ShellSeams.ReadLocalStatus is not null)
        {
            dto = ShellSeams.ReadLocalStatus();
            return dto is not null;
        }

        try
        {
            var request = WebRequest.Create(Url);
            request.Method = "GET";
            request.Timeout = 3000;
            using var response = (HttpWebResponse)request.GetResponse();
            if ((int)response.StatusCode != 200)
            {
                return false;
            }

            using var stream = response.GetResponseStream();
            using var reader = new StreamReader(stream!);
            var json = JsonDocument.Parse(reader.ReadToEnd());
            dto = new LocalServerStatusDto
            {
                StartedAtUtc = json.RootElement.TryGetProperty("startedAtUtc", out var s) && s.ValueKind != JsonValueKind.Null
                    ? s.GetDateTimeOffset()
                    : null,
                DbOk = json.RootElement.TryGetProperty("dbOk", out var d) && d.GetBoolean(),
            };
            return true;
        }
        catch
        {
            return false;
        }
    }
}