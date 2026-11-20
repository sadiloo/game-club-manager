using System.Text.Json;

namespace GameClub.Kiosk.Core.Runtime;

/// <summary>Customer kiosk login error keys and placeholder display text (single swap file for Persian).</summary>
public static class KioskLoginUserMessages
{
    public const string PlaceholderPrefix = "PLACEHOLDER:";

    public static class Keys
    {
        public const string PairBefore = "pair_before";
        public const string Unreachable = "unreachable";
        public const string Timeout = "timeout";
        public const string HttpGeneric = "http_generic";
        public const string Unknown = "unknown";
        public const string EmptyFields = "empty_fields";

        public const string InvalidLogin = "invalid_login";
        public const string DeviceAuth = "device_auth";
        public const string AccountLocked = "account_locked";
        public const string NoCredit = "no_credit";
        public const string DebtBlocked = "debt_blocked";
        public const string StaleStation = "stale_station";
        public const string SessionElsewhere = "session_elsewhere";
        public const string TestMode = "test_mode";
        public const string StationBusy = "station_busy";
    }

    private static readonly HashSet<string> AllMappedKeys = new(StringComparer.Ordinal)
    {
        Keys.PairBefore,
        Keys.Unreachable,
        Keys.Timeout,
        Keys.HttpGeneric,
        Keys.Unknown,
        Keys.EmptyFields,
        Keys.InvalidLogin,
        Keys.DeviceAuth,
        Keys.AccountLocked,
        Keys.NoCredit,
        Keys.DebtBlocked,
        Keys.StaleStation,
        Keys.SessionElsewhere,
        Keys.TestMode,
        Keys.StationBusy,
    };

    private static readonly HashSet<string> ServerSemanticKeys = new(StringComparer.Ordinal)
    {
        Keys.InvalidLogin,
        Keys.DeviceAuth,
        Keys.AccountLocked,
        Keys.NoCredit,
        Keys.DebtBlocked,
        Keys.StaleStation,
        Keys.SessionElsewhere,
        Keys.TestMode,
        Keys.StationBusy,
    };

    private static readonly Dictionary<string, string> PlaceholderByKey = new(StringComparer.Ordinal)
    {
        [Keys.PairBefore] = PlaceholderPrefix + Keys.PairBefore,
        [Keys.Unreachable] = PlaceholderPrefix + Keys.Unreachable,
        [Keys.Timeout] = PlaceholderPrefix + Keys.Timeout,
        [Keys.HttpGeneric] = PlaceholderPrefix + Keys.HttpGeneric,
        [Keys.Unknown] = PlaceholderPrefix + Keys.Unknown,
        [Keys.EmptyFields] = PlaceholderPrefix + Keys.EmptyFields,
        [Keys.InvalidLogin] = PlaceholderPrefix + Keys.InvalidLogin,
        [Keys.DeviceAuth] = PlaceholderPrefix + Keys.DeviceAuth,
        [Keys.AccountLocked] = PlaceholderPrefix + Keys.AccountLocked,
        [Keys.NoCredit] = PlaceholderPrefix + Keys.NoCredit,
        [Keys.DebtBlocked] = PlaceholderPrefix + Keys.DebtBlocked,
        [Keys.StaleStation] = PlaceholderPrefix + Keys.StaleStation,
        [Keys.SessionElsewhere] = PlaceholderPrefix + Keys.SessionElsewhere,
        [Keys.TestMode] = PlaceholderPrefix + Keys.TestMode,
        [Keys.StationBusy] = PlaceholderPrefix + Keys.StationBusy,
    };

    public static IReadOnlyCollection<string> MappedKeys => AllMappedKeys;

    public static string DisplayText(string key)
        => PlaceholderByKey.TryGetValue(key, out var text)
            ? text
            : PlaceholderByKey[Keys.Unknown];

    public static bool IsKnownKey(string key) => AllMappedKeys.Contains(key);

    public static string? TryGetEmptyFieldsKey(string loginName, string password)
    {
        if (string.IsNullOrWhiteSpace(loginName) || string.IsNullOrWhiteSpace(password))
        {
            return Keys.EmptyFields;
        }

        return null;
    }

    /// <summary>Whether the kiosk host should issue POST /api/kiosk/auth/login (display gate only).</summary>
    public static bool LoginHttpWouldBeSent(bool isPaired, string loginName, string password)
        => isPaired && TryGetEmptyFieldsKey(loginName, password) is null;

    public enum LoginTransportFailure
    {
        Unreachable,
        Timeout,
    }

    public static string ResolveFailureKey(string? responseBody, int httpStatus, LoginTransportFailure? transport = null)
    {
        if (transport == LoginTransportFailure.Unreachable)
        {
            return Keys.Unreachable;
        }

        if (transport == LoginTransportFailure.Timeout)
        {
            return Keys.Timeout;
        }

        var serverCode = TryParseServerCode(responseBody);
        if (serverCode is { Length: > 0 })
        {
            return ServerSemanticKeys.Contains(serverCode) ? serverCode : Keys.Unknown;
        }

        if (httpStatus >= 400)
        {
            return Keys.HttpGeneric;
        }

        return Keys.Unknown;
    }

    public static string? TryParseServerCode(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody!);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
            {
                var code = codeEl.GetString();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    return code;
                }
            }

            if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
            {
                var error = errorEl.GetString();
                if (!string.IsNullOrWhiteSpace(error))
                {
                    return error;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

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