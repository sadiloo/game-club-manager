using System.Globalization;
using System.Text;

namespace GameClub.Kiosk.Core.Runtime;

/// <summary>Kiosk StatusText copy and composition (spec §15.3–15.4).</summary>
public static class KioskStatusMessages
{
    public static class Keys
    {
        public const string Unpaired = "kiosk.status.unpaired";
        public const string Paired = "kiosk.status.paired";
        public const string Station = "kiosk.status.station";
        public const string Locked = "kiosk.status.locked";
        public const string LockedLeaseExpired = "kiosk.status.lockedLeaseExpired";
        public const string Playing = "kiosk.status.playing";
        public const string ConnConnected = "kiosk.status.conn.connected";
        public const string ConnConnecting = "kiosk.status.conn.connecting";
        public const string ConnReconnecting = "kiosk.status.conn.reconnecting";
        public const string ConnNotConnected = "kiosk.status.conn.notConnected";
        public const string ConnNoResponse = "kiosk.status.conn.noResponse";
        public const string TimeLeft = "kiosk.status.timeLeft";
        public const string ErrLaunch = "kiosk.status.err.launch";
        public const string ErrCatalog = "kiosk.status.err.catalog";
        public const string ErrGeneric = "kiosk.status.err.generic";
        public const string Join = "kiosk.status.join";
        public const string Sep = "kiosk.status.sep";
    }

    private static readonly HashSet<string> AllMappedKeys = new(StringComparer.Ordinal)
    {
        Keys.Unpaired,
        Keys.Paired,
        Keys.Station,
        Keys.Locked,
        Keys.LockedLeaseExpired,
        Keys.Playing,
        Keys.ConnConnected,
        Keys.ConnConnecting,
        Keys.ConnReconnecting,
        Keys.ConnNotConnected,
        Keys.ConnNoResponse,
        Keys.TimeLeft,
        Keys.ErrLaunch,
        Keys.ErrCatalog,
        Keys.ErrGeneric,
        Keys.Join,
        Keys.Sep,
    };

    private static readonly Dictionary<string, string> TextByKey = new(StringComparer.Ordinal)
    {
        [Keys.Unpaired] = "این سیستم هنوز جفت نشده است. کد جفت‌سازی را از اپراتور بگیرید.",
        [Keys.Paired] = "جفت شد: سیستم {station}",
        [Keys.Station] = "سیستم {station}",
        [Keys.Locked] = "سیستم {station} قفل است",
        [Keys.LockedLeaseExpired] = "سیستم {station} قفل شد، چون مدتی به سرور وصل نبود",
        [Keys.Playing] = "در حال بازی روی سیستم {station}",
        [Keys.ConnConnected] = "وصل به سرور",
        [Keys.ConnConnecting] = "در حال اتصال…",
        [Keys.ConnReconnecting] = "در حال اتصال دوباره…",
        [Keys.ConnNotConnected] = "قطع از سرور",
        [Keys.ConnNoResponse] = "سرور پاسخ نمی‌دهد",
        [Keys.TimeLeft] = "حدود {time} مانده",
        [Keys.ErrLaunch] = "بازی باز نشد. دوباره امتحان کنید؛ اگر تکرار شد، به پیشخوان خبر بدهید.",
        [Keys.ErrCatalog] = "فهرست بازی‌ها به‌روز نشد.",
        [Keys.ErrGeneric] = "مشکلی پیش آمد. لطفاً به پیشخوان خبر بدهید.",
        [Keys.Join] = "، ",
        [Keys.Sep] = " · ",
    };

    public static IReadOnlyCollection<string> MappedKeys => AllMappedKeys;

    public static string DisplayText(string key)
        => TextByKey.TryGetValue(key, out var text) ? text : string.Empty;

    public static string ConnectionTextKey(KioskStatusConnectionKind kind)
        => kind switch
        {
            KioskStatusConnectionKind.NotConnected => Keys.ConnNotConnected,
            KioskStatusConnectionKind.NoResponse => Keys.ConnNoResponse,
            KioskStatusConnectionKind.Reconnecting => Keys.ConnReconnecting,
            KioskStatusConnectionKind.Connected => Keys.ConnConnected,
            KioskStatusConnectionKind.Connecting => Keys.ConnConnecting,
            KioskStatusConnectionKind.TimeRemaining => Keys.TimeLeft,
            _ => Keys.ConnNotConnected,
        };

    public static string FormatPersianTime(TimeSpan remaining)
    {
        var ascii = string.Format(CultureInfo.InvariantCulture, "{0:hh\\:mm\\:ss}", remaining);
        return ToPersianDigits(ascii);
    }

    public static string ToPersianDigits(string asciiDigits)
    {
        var sb = new StringBuilder(asciiDigits.Length);
        foreach (var ch in asciiDigits)
        {
            sb.Append(ch is >= '0' and <= '9' ? (char)('\u06F0' + (ch - '0')) : ch);
        }

        return sb.ToString();
    }

    public static string FormatTimeLeftSuffix(TimeSpan remaining)
    {
        var template = DisplayText(Keys.TimeLeft);
        return template.Replace("{time}", FormatPersianTime(remaining));
    }

    public static string FormatStationTemplate(string templateKey, string stationId)
        => DisplayText(templateKey).Replace("{station}", stationId);

    public static bool IsBareConnectedConnection(KioskStatusConnectionKind? kind)
        => kind == KioskStatusConnectionKind.Connected;
}