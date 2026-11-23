using System.Text.Json;

namespace GameClub.Kiosk.Core.Security;

public static class KioskTrustFileWriteValidator
{
    public static string? ValidatePayload(string? expectedKioskSid, string? callerSid, string? fileName, string? content)
    {
        if (string.IsNullOrWhiteSpace(expectedKioskSid))
        {
            return "guard_sid_unconfigured";
        }

        var expected = NormalizeSid(expectedKioskSid!);
        var caller = callerSid is null ? "" : NormalizeSid(callerSid);
        if (string.IsNullOrWhiteSpace(callerSid)
            || !string.Equals(caller, expected, StringComparison.OrdinalIgnoreCase))
        {
            return "caller_sid_denied";
        }

        if (!TryNormalizeTrustFileName(fileName, out _))
        {
            return "invalid_file_name";
        }

        if (content is null)
        {
            return "missing_content";
        }

        var byteCount = System.Text.Encoding.UTF8.GetByteCount(content);
        if (byteCount > KioskTrustFiles.MaxContentBytes)
        {
            return "content_too_large";
        }

        try
        {
            JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return "invalid_json";
        }

        return null;
    }

    public static bool TryNormalizeTrustFileName(string? fileName, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (!string.Equals(fileName, fileName.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        var trimmed = fileName;
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || trimmed.IndexOf("..", StringComparison.Ordinal) >= 0
            || trimmed.IndexOf('/') >= 0
            || trimmed.IndexOf('\\') >= 0
            || Path.IsPathRooted(trimmed))
        {
            return false;
        }

        if (string.Equals(trimmed, KioskTrustFiles.SetupFileName, StringComparison.OrdinalIgnoreCase))
        {
            canonical = KioskTrustFiles.SetupFileName;
            return true;
        }

        if (string.Equals(trimmed, KioskTrustFiles.AdminLockFileName, StringComparison.OrdinalIgnoreCase))
        {
            canonical = KioskTrustFiles.AdminLockFileName;
            return true;
        }

        return false;
    }

    private static string NormalizeSid(string sid)
    {
        var trimmed = sid.Trim();
        return trimmed.StartsWith("S-", StringComparison.OrdinalIgnoreCase) ? trimmed.ToUpperInvariant() : trimmed;
    }
}