using System.Text.Json;

namespace GameClub.Kiosk.Core.Runtime;

/// <summary>Customer kiosk login error keys and Persian display text (spec §6.5, kiosk.login.err.*).</summary>
public static class KioskLoginUserMessages
{
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

    private static readonly Dictionary<string, string> TextByKey = new(StringComparer.Ordinal)
    {
        [Keys.EmptyFields] = "نام کاربری و رمز را وارد کنید.",
        [Keys.PairBefore] = "این سیستم هنوز آماده نیست. لطفاً به پیشخوان خبر بدهید.",
        [Keys.Unreachable] = "به سرور وصل نشد. چند لحظه بعد دوباره امتحان کنید؛ اگر درست نشد، به پیشخوان خبر بدهید.",
        [Keys.Timeout] = "سرور دیر جواب داد. لطفاً دوباره امتحان کنید.",
        [Keys.HttpGeneric] = "ورود انجام نشد. دوباره امتحان کنید؛ اگر تکرار شد، به پیشخوان خبر بدهید.",
        [Keys.Unknown] = "مشکلی پیش آمد. لطفاً به پیشخوان خبر بدهید.",
        [Keys.InvalidLogin] = "نام کاربری یا رمز اشتباه است.",
        [Keys.DeviceAuth] = "این سیستم به سرور شناسانده نشده است. لطفاً به پیشخوان خبر بدهید.",
        [Keys.AccountLocked] = "به‌خاطر چند رمز اشتباه، حساب شما قفل شد. برای باز شدنش به پیشخوان بروید.",
        [Keys.NoCredit] = "اعتبار شما برای بازی کافی نیست. برای شارژ به پیشخوان بروید.",
        [Keys.DebtBlocked] = "حساب شما بدهی دارد. لطفاً اول در پیشخوان تسویه کنید.",
        [Keys.StaleStation] = "این سیستم درست تنظیم نشده است. لطفاً به پیشخوان خبر بدهید.",
        [Keys.SessionElsewhere] = "شما روی سیستم دیگری بازی باز دارید. برای جابه‌جایی به پیشخوان بروید.",
        [Keys.TestMode] = "این سیستم الان در حالت آزمایش است. لطفاً به پیشخوان خبر بدهید.",
        [Keys.StationBusy] = "این سیستم دست مشتری دیگری است. لطفاً به پیشخوان خبر بدهید.",
    };

    public static IReadOnlyCollection<string> MappedKeys => AllMappedKeys;

    public static string DisplayText(string key)
    {
        if (string.Equals(key, KioskStatusMessages.Keys.ServerUnreachable, StringComparison.Ordinal))
        {
            return KioskStatusMessages.DisplayText(key);
        }

        return TextByKey.TryGetValue(key, out var text)
            ? text
            : TextByKey[Keys.Unknown];
    }

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
        if (transport is LoginTransportFailure.Unreachable or LoginTransportFailure.Timeout)
        {
            return KioskStatusMessages.Keys.ServerUnreachable;
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

    /// <summary>Parses a 200 login body; on failure <paramref name="failureKey"/> is <see cref="Keys.Unknown"/>.</summary>
    public static bool TryParseLoginSessionId(string? body, string? contentType, out Guid sessionId, out string failureKey)
    {
        sessionId = Guid.Empty;
        failureKey = Keys.Unknown;
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(contentType)
            && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body!);
            if (!doc.RootElement.TryGetProperty("sessionId", out var sessionEl))
            {
                return false;
            }

            return sessionEl.ValueKind switch
            {
                JsonValueKind.Null => false,
                JsonValueKind.String when Guid.TryParse(sessionEl.GetString(), out sessionId)
                    && sessionId != Guid.Empty => true,
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string AppendToLockLine(string lockLineText, string loginError)
        => string.Concat(lockLineText, KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.Sep), loginError);

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