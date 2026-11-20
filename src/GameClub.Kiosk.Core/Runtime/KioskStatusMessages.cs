using System.Globalization;

namespace GameClub.Kiosk.Core.Runtime;

/// <summary>Customer kiosk status-line keys (English placeholders until spec v2.10 Persian copy).</summary>
public static class KioskStatusMessages
{
    public static class Keys
    {
        public const string StatusLineSeparator = "status_line_separator";
        public const string UnpairedEnterPairingCode = "unpaired_enter_pairing_code";
        public const string UnpairedUseOperatorPairing = "unpaired_use_operator_pairing";
        public const string StationLinePrefix = "station_line_prefix";
        public const string StationLineNotConnected = "station_line_not_connected";
        public const string StationLineNoServerResponse = "station_line_no_server_response";
        public const string StationLineTimeRemaining = "station_line_time_remaining";
        public const string StationLineTransportError = "station_line_transport_error";
        public const string StationLineReconnecting = "station_line_reconnecting";
        public const string StationLineConnected = "station_line_connected";
        public const string StationLineConnecting = "station_line_connecting";
        public const string LockedActive = "locked_active";
        public const string LockedLeaseExpired = "locked_lease_expired";
        public const string PlayingOn = "playing_on";
        public const string PairedTo = "paired_to";
        public const string LaunchFailed = "launch_failed";
        public const string CatalogRefreshFailed = "catalog_refresh_failed";
        public const string StationLineWithDetail = "station_line_with_detail";
    }

    private static readonly HashSet<string> AllMappedKeys = new(StringComparer.Ordinal)
    {
        Keys.StatusLineSeparator,
        Keys.UnpairedEnterPairingCode,
        Keys.UnpairedUseOperatorPairing,
        Keys.StationLinePrefix,
        Keys.StationLineNotConnected,
        Keys.StationLineNoServerResponse,
        Keys.StationLineTimeRemaining,
        Keys.StationLineTransportError,
        Keys.StationLineReconnecting,
        Keys.StationLineConnected,
        Keys.StationLineConnecting,
        Keys.LockedActive,
        Keys.LockedLeaseExpired,
        Keys.PlayingOn,
        Keys.PairedTo,
        Keys.LaunchFailed,
        Keys.CatalogRefreshFailed,
        Keys.StationLineWithDetail,
    };

    private static readonly HashSet<string> TemplateKeys = new(StringComparer.Ordinal)
    {
        Keys.StationLinePrefix,
        Keys.StationLineNotConnected,
        Keys.StationLineNoServerResponse,
        Keys.StationLineTimeRemaining,
        Keys.StationLineTransportError,
        Keys.StationLineReconnecting,
        Keys.StationLineConnected,
        Keys.StationLineConnecting,
        Keys.LockedActive,
        Keys.LockedLeaseExpired,
        Keys.PlayingOn,
        Keys.PairedTo,
        Keys.LaunchFailed,
        Keys.CatalogRefreshFailed,
        Keys.StationLineWithDetail,
    };

    private static readonly Dictionary<string, string> TextByKey = new(StringComparer.Ordinal)
    {
        [Keys.StatusLineSeparator] = " · ",
        [Keys.UnpairedEnterPairingCode] = "Unpaired — enter operator pairing code",
        [Keys.UnpairedUseOperatorPairing] = "Unpaired — use operator pairing code",
    };

    private static readonly Dictionary<string, string> TemplateByKey = new(StringComparer.Ordinal)
    {
        [Keys.StationLinePrefix] = "Station {0} — ",
        [Keys.StationLineNotConnected] = "Station {0} — not connected",
        [Keys.StationLineNoServerResponse] = "Station {0} — no server response",
        [Keys.StationLineTimeRemaining] = "Station {0} — ~{1} left",
        [Keys.StationLineTransportError] = "Station {0} — {1}",
        [Keys.StationLineReconnecting] = "Station {0} — reconnecting…",
        [Keys.StationLineConnected] = "Station {0} — connected",
        [Keys.StationLineConnecting] = "Station {0} — connecting…",
        [Keys.LockedActive] = "Locked — {0}",
        [Keys.LockedLeaseExpired] = "Locked — lease expired ({0})",
        [Keys.PlayingOn] = "Playing on {0}",
        [Keys.PairedTo] = "Paired to {0}",
        [Keys.LaunchFailed] = "Launch failed: {0}",
        [Keys.CatalogRefreshFailed] = "Station {0} — catalog refresh failed: {1}",
        [Keys.StationLineWithDetail] = "Station {0} — {1}",
    };

    public static IReadOnlyCollection<string> MappedKeys => AllMappedKeys;

    public static string DisplayText(string key)
        => TextByKey.TryGetValue(key, out var text)
            ? text
            : FormatTemplate(key);

    public static string FormatTemplate(string key, params object[] args)
    {
        if (!TemplateByKey.TryGetValue(key, out var template))
        {
            return TextByKey.TryGetValue(key, out var literal) ? literal : string.Empty;
        }

        return string.Format(CultureInfo.InvariantCulture, template, args);
    }

    public static string FormatStationLine(string lineKey, string isolatedStationId)
        => FormatTemplate(lineKey, isolatedStationId);

    public static string FormatStationWithDetail(string stationId, string detail)
        => FormatTemplate(Keys.StationLineWithDetail, stationId, detail);

    public static string FormatStationTransportError(string isolatedStationId, string detail)
        => FormatTemplate(Keys.StationLineTransportError, isolatedStationId, detail);

    public static string FormatTimeRemaining(string isolatedStationId, TimeSpan remaining)
    {
        var formatted = string.Format(CultureInfo.InvariantCulture, "{0:hh\\:mm\\:ss}", remaining);
        return FormatTemplate(Keys.StationLineTimeRemaining, isolatedStationId, formatted);
    }

    public static string FormatLockedLine(string lockKey, string isolatedStationId, string connectionSuffix)
    {
        var locked = FormatTemplate(lockKey, isolatedStationId);
        return string.Concat(locked, DisplayText(Keys.StatusLineSeparator), connectionSuffix);
    }

    public static string StationLinePrefixFor(string isolatedStationId)
        => FormatTemplate(Keys.StationLinePrefix, isolatedStationId);

    /// <summary>Resolved sample text for each mapped key (contract tests only).</summary>
    public static string PreviewText(string key)
    {
        var pc = KioskStationStatus.IsolateStationId("PC-1");
        return key switch
        {
            Keys.StatusLineSeparator => DisplayText(key),
            Keys.UnpairedEnterPairingCode => DisplayText(key),
            Keys.UnpairedUseOperatorPairing => DisplayText(key),
            Keys.StationLinePrefix => StationLinePrefixFor(pc),
            Keys.StationLineNotConnected => FormatStationLine(key, pc),
            Keys.StationLineNoServerResponse => FormatStationLine(key, pc),
            Keys.StationLineTimeRemaining => FormatTimeRemaining(pc, TimeSpan.FromSeconds(3661)),
            Keys.StationLineTransportError => FormatStationTransportError(pc, "transport error"),
            Keys.StationLineReconnecting => FormatStationLine(key, pc),
            Keys.StationLineConnected => FormatStationLine(key, pc),
            Keys.StationLineConnecting => FormatStationLine(key, pc),
            Keys.LockedActive => FormatTemplate(key, pc),
            Keys.LockedLeaseExpired => FormatTemplate(key, pc),
            Keys.PlayingOn => FormatTemplate(key, "PC-1"),
            Keys.PairedTo => FormatTemplate(key, "PC-1"),
            Keys.LaunchFailed => FormatTemplate(key, "access denied"),
            Keys.CatalogRefreshFailed => FormatTemplate(key, "PC-1", "timeout"),
            Keys.StationLineWithDetail => FormatStationWithDetail("PC-1", "detail"),
            _ => string.Empty,
        };
    }
}