namespace GameClub.Kiosk.Core.Runtime;

/// <summary>User-facing kiosk login error text (lock line). Centralize for locale swap.</summary>
public static class KioskLoginUserMessages
{
    public const string PairBeforeLogin = "Pair before login";

    public const string Unreachable = "Login failed (server unreachable)";

    public const string Timeout = "Login failed (timeout)";

    public static string HttpStatus(int statusCode) => $"Login failed (HTTP {statusCode})";

    public static string AppendToLockLine(string lockLineText, string loginError)
        => string.Concat(lockLineText, " · ", loginError);

    public static bool ExposesCredential(string composedLockLine, string? password, string? deviceKey)
    {
        if (string.IsNullOrEmpty(composedLockLine))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(password) && composedLockLine.Contains(password, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(deviceKey) && composedLockLine.Contains(deviceKey, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}