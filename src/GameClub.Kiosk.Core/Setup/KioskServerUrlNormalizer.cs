using System.Globalization;
using System.Text;

namespace GameClub.Kiosk.Core.Setup;

public static class KioskServerUrlNormalizer
{
    public static bool TryNormalize(string? input, out string normalized, out string? errorCode)
    {
        normalized = "";
        errorCode = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            errorCode = "empty";
            return false;
        }

        var text = ToLatinDigits(input!.Trim());
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            errorCode = "invalid";
            return false;
        }

        var port = uri.IsDefaultPort ? 5000 : uri.Port;
        var host = uri.Host;
        normalized = $"{uri.Scheme}://{host}:{port}".TrimEnd('/');
        return true;
    }

    public static string ToLatinDigits(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is >= '\u06F0' and <= '\u06F9')
            {
                sb.Append((char)('0' + (ch - '\u06F0')));
            }
            else if (ch is >= '\u0660' and <= '\u0669')
            {
                sb.Append((char)('0' + (ch - '\u0660')));
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}