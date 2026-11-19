using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameClub.Server.Diagnostics;

internal static partial class ProbeRequestLog
{
    private static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("GAMECLUB_PROBE_HTTP_LOG"), "1", StringComparison.Ordinal);

    internal static bool IsEnabled => Enabled;

    internal static long UtcMs() =>
        (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

    internal static void WriteLine(string message)
    {
        if (!Enabled)
        {
            return;
        }

        Console.WriteLine(message);
        Console.Out.Flush();
    }

    internal static string RedactBody(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return "";
        }

        var trimmed = body.Length > 4096 ? body[..4096] + "…(truncated)" : body;
        return SensitiveJsonPattern().Replace(trimmed, m =>
        {
            var key = m.Groups[1].Value;
            return $"\"{key}\":\"[redacted]\"";
        });
    }

    [GeneratedRegex(
        "\"(password|pin|deviceKey|token|newPassword|operatorPin)\"\\s*:\\s*\"[^\"]*\"",
        RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveJsonPattern();
}