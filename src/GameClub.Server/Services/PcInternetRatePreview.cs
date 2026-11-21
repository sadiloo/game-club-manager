using GameClub.Billing;
using GameClub.Server.Time;

namespace GameClub.Server.Services;

public static class PcInternetRatePreview
{
    public static (long? HourlyToman, string? Band) Resolve(
        RateCard card,
        string groupId,
        PlayMode targetMode,
        DateTime nowUtc)
    {
        try
        {
            var at = TimeMapping.ToTehran(nowUtc);
            var quote = RateResolver.Resolve(
                card,
                SpecialDayCalendar.Empty,
                at,
                groupId,
                targetMode,
                1);
            var band = quote.Source == RateSource.TimeBand ? "night" : "day";
            return (quote.HourlyRateToman, band);
        }
        catch (BillingException)
        {
            return (null, null);
        }
    }
}