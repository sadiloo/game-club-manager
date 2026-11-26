using System.Text;

namespace GameClub.Kiosk.Core.Runtime;

public readonly record struct KioskStatusRunSegment(string Text, bool IsolateLatin);

public sealed class KioskStatusPresentation
{
    public IReadOnlyList<KioskStatusRunSegment> StatusSegments { get; init; } = [];

    public bool ShowSeparator { get; init; }

    public IReadOnlyList<KioskStatusRunSegment> ErrorSegments { get; init; } = [];

    public string ToCombinedText()
    {
        var sb = new StringBuilder();
        foreach (var seg in StatusSegments)
        {
            sb.Append(seg.Text);
        }

        if (ShowSeparator)
        {
            sb.Append(KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.Sep));
        }

        foreach (var seg in ErrorSegments)
        {
            sb.Append(seg.Text);
        }

        return sb.ToString();
    }
}

public static class KioskStatusPresentationBuilder
{
    public static KioskStatusPresentation Build(
        KioskStatusLineState state,
        string? loginErrorText,
        string? transientErrorKey)
    {
        if (state.Root == KioskStatusRootKind.Unpaired)
        {
            if (!string.IsNullOrEmpty(loginErrorText))
            {
                return new KioskStatusPresentation
                {
                    ErrorSegments = [new KioskStatusRunSegment(loginErrorText!, IsolateLatin: false)],
                };
            }

            return new KioskStatusPresentation
            {
                StatusSegments = [new KioskStatusRunSegment(KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.Unpaired), false)],
            };
        }

        var statusSegments = BuildStatusSegments(state);
        var errorText = transientErrorKey is not null
            ? KioskStatusMessages.DisplayText(transientErrorKey)
            : loginErrorText ?? string.Empty;
        var showSep = errorText.Length > 0;
        IReadOnlyList<KioskStatusRunSegment> errorSegments = showSep
            ? [new KioskStatusRunSegment(errorText, false)]
            : [];

        return new KioskStatusPresentation
        {
            StatusSegments = statusSegments,
            ShowSeparator = showSep,
            ErrorSegments = errorSegments,
        };
    }

    private static List<KioskStatusRunSegment> BuildStatusSegments(KioskStatusLineState state)
    {
        var station = state.StationId ?? "";
        return state.Root switch
        {
            KioskStatusRootKind.Paired => ExpandStationTemplate(KioskStatusMessages.Keys.Paired, station),
            KioskStatusRootKind.Playing => ExpandStationTemplate(KioskStatusMessages.Keys.Playing, station),
            KioskStatusRootKind.StationWithConnection or KioskStatusRootKind.Locked or KioskStatusRootKind.LockedLeaseExpired
                => BuildStationPlusConnection(state),
            _ => [],
        };
    }

    private static List<KioskStatusRunSegment> BuildStationPlusConnection(KioskStatusLineState state)
    {
        var station = state.StationId ?? "";
        var segments = state.Root switch
        {
            KioskStatusRootKind.Locked => ExpandStationTemplate(KioskStatusMessages.Keys.Locked, station),
            KioskStatusRootKind.LockedLeaseExpired => ExpandStationTemplate(KioskStatusMessages.Keys.LockedLeaseExpired, station),
            _ => ExpandStationTemplate(KioskStatusMessages.Keys.Station, station),
        };

        if (state.Connection is not { } conn)
        {
            return segments;
        }

        segments.Add(new KioskStatusRunSegment(KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.Join), false));
        if (conn == KioskStatusConnectionKind.TimeRemaining && state.TimeRemaining is TimeSpan t)
        {
            segments.AddRange(ExpandTimeLeftTemplate(t));
        }
        else
        {
            var connText = KioskStatusMessages.DisplayText(KioskStatusMessages.ConnectionTextKey(conn));
            segments.Add(new KioskStatusRunSegment(connText, false));
        }

        return segments;
    }

    private static List<KioskStatusRunSegment> ExpandStationTemplate(string templateKey, string stationId)
    {
        var template = KioskStatusMessages.DisplayText(templateKey);
        var token = "{station}";
        var idx = template.IndexOf(token, StringComparison.Ordinal);
        if (idx < 0)
        {
            return [new KioskStatusRunSegment(template, false)];
        }

        var before = template.Substring(0, idx);
        var after = template.Substring(idx + token.Length);
        var list = new List<KioskStatusRunSegment>();
        if (before.Length > 0)
        {
            list.Add(new KioskStatusRunSegment(before, false));
        }

        list.Add(new KioskStatusRunSegment(stationId, IsolateLatin: true));
        if (after.Length > 0)
        {
            list.Add(new KioskStatusRunSegment(after, false));
        }

        return list;
    }

    internal static List<KioskStatusRunSegment> ExpandTimeLeftTemplate(TimeSpan remaining)
    {
        var template = KioskStatusMessages.DisplayText(KioskStatusMessages.Keys.TimeLeft);
        const string timeToken = "{time}";
        var timeValue = KioskStatusMessages.FormatPersianTime(remaining);
        var idx = template.IndexOf(timeToken, StringComparison.Ordinal);
        if (idx < 0)
        {
            return [new KioskStatusRunSegment(template, false)];
        }

        var before = template.Substring(0, idx);
        var after = template.Substring(idx + timeToken.Length);
        var list = new List<KioskStatusRunSegment>();
        if (before.Length > 0)
        {
            list.Add(new KioskStatusRunSegment(before, false));
        }

        list.Add(new KioskStatusRunSegment(timeValue, IsolateLatin: true));
        if (after.Length > 0)
        {
            list.Add(new KioskStatusRunSegment(after, false));
        }

        return list;
    }
}