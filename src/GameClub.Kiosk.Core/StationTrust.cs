namespace GameClub.Kiosk.Core;

/// <summary>
/// The cafe server is reached only over HTTPS, and the station pins the cafe CA
/// (or the server certificate) by thumbprint. A missing pin does not mean "trust the LAN".
/// </summary>
public static class StationTrust
{
    public static string? RejectHub(string? hubUrl, string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(hubUrl))
        {
            return "آدرس سرور تنظیم نشده است.";
        }

        var url = hubUrl!.Trim();
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return "ارتباط با سرور باید https باشد.";
        }

        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return "اثرانگشت گواهی سرور تنظیم نشده است.";
        }

        if (Normalize(thumbprint).Length == 0)
        {
            return "اثرانگشت گواهی سرور تنظیم نشده است.";
        }

        return null;
    }

    public static bool MatchesPresented(string? expected, string? leafThumbprint, IEnumerable<string>? chainThumbprints)
    {
        if (ThumbprintEquals(expected, leafThumbprint))
        {
            return true;
        }

        if (chainThumbprints is null)
        {
            return false;
        }

        foreach (var thumb in chainThumbprints)
        {
            if (ThumbprintEquals(expected, thumb))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ThumbprintEquals(string? expected, string? presented)
    {
        var left = Normalize(expected);
        var right = Normalize(presented);
        if (left.Length == 0 || right.Length == 0 || left.Length != right.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < left.Length; i++)
        {
            diff |= left[i] ^ right[i];
        }

        return diff == 0;
    }

    public static string Normalize(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            return "";
        }

        var value = thumbprint!;
        var chars = new char[value.Length];
        var n = 0;
        foreach (var ch in value)
        {
            if (ch == ' ' || ch == ':' || ch == '-')
            {
                continue;
            }

            chars[n++] = char.ToUpperInvariant(ch);
        }

        return new string(chars, 0, n);
    }
}
