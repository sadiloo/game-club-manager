namespace GameClub.Billing;

/// <summary>Resolves per-minute game tariffs from segment snapshots (SS-14).</summary>
public static class GameTariffPricing
{
    public static bool HasGameSnapshot(long? gameHourlyRateToman, int? gameTitleId)
        => gameTitleId is not null && gameHourlyRateToman is not null;

    public static RateQuote Resolve(
        RateCard card,
        DateTimeOffset instant,
        string groupId,
        long gameHourlyRateToman,
        long? gameNightHourlyRateToman)
    {
        instant = TehranTime.Ensure(instant);
        var time = TehranTime.TimeOfDay(instant);
        if (gameNightHourlyRateToman is long night && night > 0)
        {
            var band = card.BandAt(groupId, time);
            if (band is not null)
            {
                return new RateQuote(night, RateSource.Game, band.Id);
            }
        }

        return new RateQuote(gameHourlyRateToman, RateSource.Game, null);
    }

  public static DateTimeOffset NextBoundary(
        DateTimeOffset cursor,
        RateCard card,
        string groupId,
        bool gameNightRateConfigured)
    {
        if (!gameNightRateConfigured)
        {
            return TehranTime.NextMidnight(cursor);
        }

        return RateBoundaries.Next(cursor, card, groupId);
    }
}