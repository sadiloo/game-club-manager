using System.Globalization;
using GameClub.Billing;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;

namespace GameClub.Server.Operator;

internal static class PcInternetBillDisplay
{
    public static string? FormatBillSegment(
        PlaySegmentEntity segment,
        long hourlyToman,
        SessionCharge charge,
        int billedMinutes,
        long amountToman)
    {
        if (segment.Kind != StationKind.Pc)
        {
            return null;
        }

        var on = segment.Mode == PlayMode.Online;
        var band = on ? "آنلاین" : "آفلاین";
        var start = TimeMapping.ToTehran(segment.StartUtc);
        var endUtc = segment.EndUtc ?? charge.Lines.LastOrDefault()?.End.UtcDateTime ?? segment.StartUtc;
        var end = TimeMapping.ToTehran(endUtc);
        var rate = Ss14BillDisplay.ToPersianDigits(
            hourlyToman.ToString("N0", CultureInfo.InvariantCulture).Replace(",", "٬", StringComparison.Ordinal));
        var time = Ss14BillDisplay.ToPersianDigits(
            $"{start.Hour:00}:{start.Minute:00}–{end.Hour:00}:{end.Minute:00}");
        var mins = Ss14BillDisplay.ToPersianDigits(billedMinutes.ToString(CultureInfo.InvariantCulture));
        var amt = Ss14BillDisplay.ToPersianDigits(
            amountToman.ToString("N0", CultureInfo.InvariantCulture).Replace(",", "٬", StringComparison.Ordinal));
        var state = on ? "اینترنت وصل" : "اینترنت قطع";
        return $"{state} · {rate} تومان/ساعت ({band}) · {time} · {mins} دقیقه · {amt}";
    }
}