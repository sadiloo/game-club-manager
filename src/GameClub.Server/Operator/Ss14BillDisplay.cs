using System.Globalization;
using GameClub.Billing;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;

namespace GameClub.Server.Operator;

internal static class Ss14BillDisplay
{
    public static string ToPersianDigits(string text)
    {
        const string fa = "۰۱۲۳۴۵۶۷۸۹";
        return string.Concat(text.Select(c => c is >= '0' and <= '9' ? fa[c - '0'] : c));
    }

    public static string FormatGameLine(
        string gameName,
        int playerCount,
        long hourlyToman,
        DateTimeOffset startTehran,
        DateTimeOffset endTehran)
    {
        var start = TehranTime.TimeOfDay(startTehran);
        var end = TehranTime.TimeOfDay(endTehran);
        var rate = FormatTomanAmount(hourlyToman);
        var time = $"{start.Hour:00}:{start.Minute:00}–{end.Hour:00}:{end.Minute:00}";
        var pc = playerCount.ToString();
        return ToPersianDigits($"{gameName} · {pc} دسته · @ {rate} · {time}");
    }

    public static string? FormatBillSegment(
        PlaySegmentEntity segment,
        long hourlyToman,
        SessionCharge charge)
    {
        if (segment.GameTitleId is not int gameId)
        {
            return null;
        }

        var name = segment.GameTitle?.Name;
        if (string.IsNullOrEmpty(name))
        {
            name = charge.Lines.FirstOrDefault(l => l.GameTitleId == gameId)?.GameDisplayName;
        }

        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var endUtc = segment.EndUtc ?? charge.Lines.LastOrDefault()?.End.UtcDateTime ?? segment.StartUtc;
        var start = TimeMapping.ToTehran(segment.StartUtc);
        var end = TimeMapping.ToTehran(endUtc);
        return FormatGameLine(name, segment.PlayerCount, hourlyToman, start, end);
    }

    private static string FormatTomanAmount(long toman)
    {
        var latin = toman.ToString("N0", CultureInfo.InvariantCulture).Replace(",", "٬", StringComparison.Ordinal);
        return ToPersianDigits(latin);
    }

    public static InvoiceTimeLineEntity ToInvoiceTimeLine(Guid invoiceId, PricedSlice line)
    {
        var start = TehranTime.Ensure(line.Start);
        var end = TehranTime.Ensure(line.End);
        string description;
        string? gameSnapshot = null;
        if (line.Source == RateSource.Game && line.GameTitleId is not null)
        {
            var name = line.GameDisplayName ?? "بازی";
            gameSnapshot = name;
            description = FormatGameLine(name, line.PlayerCount, line.HourlyRateToman, start, end);
        }
        else
        {
            description = ToPersianDigits(
                $"{line.GroupId} {line.Mode} x{line.PlayerCount} {TehranTime.TimeOfDay(start).Hour:00}:{TehranTime.TimeOfDay(start).Minute:00}-{TehranTime.TimeOfDay(end).Hour:00}:{TehranTime.TimeOfDay(end).Minute:00}");
        }

        return new InvoiceTimeLineEntity
        {
            InvoiceId = invoiceId,
            Description = description,
            AmountToman = line.AmountToman,
            GameTitleNameSnapshot = gameSnapshot,
        };
    }
}