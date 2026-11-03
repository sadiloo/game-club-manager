using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class SessionPricingTests
{
    [Fact]
    public void One_player_inside_a_single_band_uses_the_base_hourly_rate()
    {
        var charge = CafeFixture.Price(CafeFixture.Session(
            "s",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0), "ps5-1", CafeFixture.Ps5, 1)));

        var line = Assert.Single(charge.Lines);
        Assert.Equal(RateSource.BaseTariff, line.Source);
        Assert.Equal(60, line.BilledMinutes);
        Assert.Equal(GameTimerReference.Ps5OnePlayerHourly, line.AmountToman);
        Assert.Equal(110_000, charge.TotalAmountToman);

        var half = CafeFixture.Price(CafeFixture.Session(
            "h",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 10, 30), "ps5-1", CafeFixture.Ps5, 1)));
        Assert.Equal(55_000, half.TotalAmountToman);
    }

    [Fact]
    public void Partial_minute_rounding_is_configurable_and_defaults_to_ceiling()
    {
        var start = T.At(2026, 6, 2, 10, 0);
        var oneSecond = CafeFixture.Session("s", CafeFixture.Play("a", start, T.At(2026, 6, 2, 10, 0, 1), "ps5-1", CafeFixture.Ps5, 1));

        var ceiling = CafeFixture.Price(oneSecond, BillingPolicy.Default);
        Assert.Equal(1, ceiling.TotalBilledMinutes);
        Assert.Equal(1_833, ceiling.TotalAmountToman);

        var floor = CafeFixture.Price(oneSecond, new BillingPolicy(1, PartialUnitMode.Floor, RateRemainderMode.Nearest));
        Assert.Equal(0, floor.TotalBilledMinutes);
        Assert.Equal(0, floor.TotalAmountToman);

        var ninety = CafeFixture.Session("s", CafeFixture.Play("a", start, T.At(2026, 6, 2, 10, 1, 30), "ps5-1", CafeFixture.Ps5, 1));
        Assert.Equal(2, CafeFixture.Price(ninety, new BillingPolicy(1, PartialUnitMode.Ceiling, RateRemainderMode.Floor)).TotalBilledMinutes);
        Assert.Equal(1, CafeFixture.Price(ninety, new BillingPolicy(1, PartialUnitMode.Floor, RateRemainderMode.Floor)).TotalBilledMinutes);
        Assert.Equal(2, CafeFixture.Price(ninety, new BillingPolicy(1, PartialUnitMode.Nearest, RateRemainderMode.Floor)).TotalBilledMinutes);

        var quarterHour = new BillingPolicy(15, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var fourteen = CafeFixture.Session("s", CafeFixture.Play("a", start, T.At(2026, 6, 2, 10, 14), "ps5-1", CafeFixture.Ps5, 1));
        Assert.Equal(15, CafeFixture.Price(fourteen, quarterHour).TotalBilledMinutes);
        Assert.Equal(27_500, CafeFixture.Price(fourteen, quarterHour).TotalAmountToman);
        var exactBlock = CafeFixture.Session("s", CafeFixture.Play("a", start, T.At(2026, 6, 2, 10, 15), "ps5-1", CafeFixture.Ps5, 1));
        Assert.Equal(15, CafeFixture.Price(exactBlock, quarterHour).TotalBilledMinutes);
        var justOver = CafeFixture.Session("s", CafeFixture.Play("a", start, T.At(2026, 6, 2, 10, 15, 1), "ps5-1", CafeFixture.Ps5, 1));
        Assert.Equal(30, CafeFixture.Price(justOver, quarterHour).TotalBilledMinutes);

        var thirty = CafeFixture.Session("s", CafeFixture.Play("a", start, T.At(2026, 6, 2, 10, 0, 30), "ps5-1", CafeFixture.Ps5, 1));
        var twentyNine = CafeFixture.Session("s", CafeFixture.Play("a", start, T.At(2026, 6, 2, 10, 0, 29), "ps5-1", CafeFixture.Ps5, 1));
        var nearest = new BillingPolicy(1, PartialUnitMode.Nearest, RateRemainderMode.Floor);
        Assert.Equal(1, CafeFixture.Price(thirty, nearest).TotalBilledMinutes);
        Assert.Equal(0, CafeFixture.Price(twentyNine, nearest).TotalBilledMinutes);
    }

    [Fact]
    public void Session_crossing_midnight_into_special_hours_splits_itself()
    {
        // One segment. Game Timer would make the operator cut this at 00:00 by hand.
        var charge = CafeFixture.Price(CafeFixture.Session(
            "night",
            CafeFixture.Play("a", T.At(2026, 6, 2, 23, 0), T.At(2026, 6, 3, 1, 30), "ps5-1", CafeFixture.Ps5, 1)));

        Assert.Equal(2, charge.Lines.Count);
        Assert.Equal(T.At(2026, 6, 3, 0, 0), charge.Lines[0].End);
        Assert.Equal(RateSource.BaseTariff, charge.Lines[0].Source);
        Assert.Equal(60, charge.Lines[0].BilledMinutes);
        Assert.Equal(110_000, charge.Lines[0].AmountToman);

        Assert.Equal(RateSource.TimeBand, charge.Lines[1].Source);
        Assert.Equal(GameTimerReference.SpecialBandId, charge.Lines[1].BandId);
        Assert.Equal(90, charge.Lines[1].BilledMinutes);
        Assert.Equal(240_000, charge.Lines[1].AmountToman);
        Assert.Equal(350_000, charge.TotalAmountToman);
        Assert.Equal(150, charge.TotalBilledMinutes);
    }

    [Fact]
    public void Leaving_special_hours_at_0830_splits_and_uses_each_rate()
    {
        var charge = CafeFixture.Price(CafeFixture.Session(
            "morning",
            CafeFixture.Play("a", T.At(2026, 6, 2, 8, 0), T.At(2026, 6, 2, 9, 0), "ps5-1", CafeFixture.Ps5, 1)));

        Assert.Equal(2, charge.Lines.Count);
        Assert.Equal(80_000, charge.Lines[0].AmountToman);
        Assert.Equal(RateSource.TimeBand, charge.Lines[0].Source);
        Assert.Equal(55_000, charge.Lines[1].AmountToman);
        Assert.Equal(RateSource.BaseTariff, charge.Lines[1].Source);
        Assert.Equal(135_000, charge.TotalAmountToman);
    }

    [Fact]
    public void Controller_change_opens_a_new_segment()
    {
        var charge = CafeFixture.Price(CafeFixture.Session(
            "swap",
            CafeFixture.Play("two", T.At(2026, 6, 2, 18, 0), T.At(2026, 6, 2, 19, 0), "ps5-1", CafeFixture.Ps5, 2),
            CafeFixture.Play("four", T.At(2026, 6, 2, 19, 0), T.At(2026, 6, 2, 20, 0), "ps5-1", CafeFixture.Ps5, 4)));

        Assert.Equal(2, charge.Lines.Count);
        Assert.Equal(2, charge.Lines[0].PlayerCount);
        Assert.Equal(125_000, charge.Lines[0].AmountToman);
        Assert.Equal(4, charge.Lines[1].PlayerCount);
        Assert.Equal(220_000, charge.Lines[1].AmountToman);
        Assert.Equal(345_000, charge.TotalAmountToman);
    }

    [Fact]
    public void Transfer_to_another_station_group_prices_each_group()
    {
        var directory = CafeFixture.Directory();
        var charge = CafeFixture.Price(CafeFixture.Session(
            "move",
            directory.Segment("on-ps5", T.At(2026, 6, 2, 18, 0), T.At(2026, 6, 2, 19, 0), "ps5-1", PlayMode.Offline, 1),
            directory.Segment("on-vip", T.At(2026, 6, 2, 19, 0), T.At(2026, 6, 2, 20, 0), "vip-1", PlayMode.Offline, 1)));

        Assert.Equal(CafeFixture.Ps5, charge.Lines[0].GroupId);
        Assert.Equal("ps5-1", charge.Lines[0].StationId);
        Assert.Equal(110_000, charge.Lines[0].AmountToman);
        Assert.Equal(CafeFixture.Vip, charge.Lines[1].GroupId);
        Assert.Equal("vip-1", charge.Lines[1].StationId);
        Assert.Equal(CafeFixture.VipOnePlayerHourly, charge.Lines[1].AmountToman);
        Assert.Equal(330_000, charge.TotalAmountToman);
    }

    [Fact]
    public void Pause_and_resume_are_excluded_from_the_charge()
    {
        var charge = CafeFixture.Price(new Session(
            "paused",
            [CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 12, 0), "ps5-1", CafeFixture.Ps5, 1)],
            [new PauseInterval(T.At(2026, 6, 2, 10, 30), T.At(2026, 6, 2, 11, 0))]));

        Assert.Equal(5_400, charge.TotalActiveSeconds);
        Assert.Equal(90, charge.TotalBilledMinutes);
        Assert.Equal(165_000, charge.TotalAmountToman);
        Assert.DoesNotContain(charge.Lines, line => line.Start < T.At(2026, 6, 2, 11, 0) && line.End > T.At(2026, 6, 2, 10, 30));

        var across = CafeFixture.Price(new Session(
            "across",
            [
                CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0), "ps5-1", CafeFixture.Ps5, 1),
                CafeFixture.Play("b", T.At(2026, 6, 2, 11, 0), T.At(2026, 6, 2, 12, 0), "ps5-1", CafeFixture.Ps5, 1),
            ],
            [new PauseInterval(T.At(2026, 6, 2, 10, 45), T.At(2026, 6, 2, 11, 15))]));
        Assert.Equal(90, across.TotalBilledMinutes);
        Assert.Equal(165_000, across.TotalAmountToman);

        var outside = CafeFixture.Price(new Session(
            "outside",
            [CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0), "ps5-1", CafeFixture.Ps5, 1)],
            [new PauseInterval(T.At(2026, 6, 2, 9, 0), T.At(2026, 6, 2, 9, 30))]));
        Assert.Equal(110_000, outside.TotalAmountToman);
    }

    [Fact]
    public void Holiday_beats_weekday_and_weekday_beats_the_hour_band()
    {
        Assert.Equal(DayOfWeek.Tuesday, new DateOnly(2026, 6, 2).DayOfWeek);
        Assert.Equal(DayOfWeek.Friday, new DateOnly(2026, 6, 5).DayOfWeek);
        Assert.Equal(DayOfWeek.Saturday, new DateOnly(2026, 6, 6).DayOfWeek);

        var card = new RateCardBuilder()
            .AddBand(GameTimerReference.SpecialHours)
            .AddBase(CafeFixture.Ps5, PlayMode.Offline, 1, 110_000)
            .AddBase(CafeFixture.Ps5, PlayMode.Offline, 2, 125_000)
            .AddBase(CafeFixture.Ps5, PlayMode.Online, null, 170_000)
            .AddBandRate(GameTimerReference.SpecialBandId, CafeFixture.Ps5, PlayMode.Offline, 1, 160_000)
            .AddBandRate(GameTimerReference.SpecialBandId, CafeFixture.Ps5, PlayMode.Offline, 2, 175_000)
            .AddBandRate(GameTimerReference.SpecialBandId, CafeFixture.Ps5, PlayMode.Online, null, 220_000)
            .AddWeekday(DayOfWeek.Friday, CafeFixture.Ps5, PlayMode.Offline, 1, 100_000)
            .AddWeekday(DayOfWeek.Saturday, CafeFixture.Ps5, PlayMode.Offline, 1, 90_000)
            .AddHoliday(CafeFixture.Ps5, PlayMode.Offline, 1, 50_000)
            .Build();
        var holidays = new HolidayCalendar([new DateOnly(2026, 6, 6)]);

        var fridaySpecial = Price(card, holidays, T.At(2026, 6, 5, 1, 0), T.At(2026, 6, 5, 2, 0), 1);
        Assert.Equal(RateSource.Weekday, Assert.Single(fridaySpecial.Lines).Source);
        Assert.Equal(100_000, fridaySpecial.TotalAmountToman);

        var fridayTwoPlayer = Price(card, holidays, T.At(2026, 6, 5, 1, 0), T.At(2026, 6, 5, 2, 0), 2);
        Assert.Equal(RateSource.TimeBand, Assert.Single(fridayTwoPlayer.Lines).Source);
        Assert.Equal(175_000, fridayTwoPlayer.TotalAmountToman);

        var saturdayHoliday = Price(card, holidays, T.At(2026, 6, 6, 1, 0), T.At(2026, 6, 6, 2, 0), 1);
        Assert.Equal(RateSource.Holiday, Assert.Single(saturdayHoliday.Lines).Source);
        Assert.Equal(50_000, saturdayHoliday.TotalAmountToman);

        var tuesdaySpecial = Price(card, holidays, T.At(2026, 6, 2, 1, 0), T.At(2026, 6, 2, 2, 0), 1);
        Assert.Equal(RateSource.TimeBand, Assert.Single(tuesdaySpecial.Lines).Source);
        Assert.Equal(160_000, tuesdaySpecial.TotalAmountToman);

        var tuesdayDay = Price(card, holidays, T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0), 1);
        Assert.Equal(RateSource.BaseTariff, Assert.Single(tuesdayDay.Lines).Source);
        Assert.Equal(110_000, tuesdayDay.TotalAmountToman);

        var fridayDay = Price(card, holidays, T.At(2026, 6, 5, 10, 0), T.At(2026, 6, 5, 11, 0), 1);
        Assert.Equal(RateSource.Weekday, Assert.Single(fridayDay.Lines).Source);
        Assert.Equal(100_000, fridayDay.TotalAmountToman);

        var onlineOnHoliday = Price(card, holidays, T.At(2026, 6, 6, 1, 0), T.At(2026, 6, 6, 2, 0), 1, PlayMode.Online);
        Assert.Equal(RateSource.TimeBand, Assert.Single(onlineOnHoliday.Lines).Source);
        Assert.Equal(220_000, onlineOnHoliday.TotalAmountToman);

        var crossing = SessionPricer.Price(
            CafeFixture.Session(
                "cross",
                CafeFixture.Play("a", T.At(2026, 6, 5, 23, 0), T.At(2026, 6, 6, 1, 0), "ps5-1", CafeFixture.Ps5, 1)),
            card,
            holidays);
        Assert.Equal(2, crossing.Lines.Count);
        Assert.Equal(RateSource.Weekday, crossing.Lines[0].Source);
        Assert.Equal(100_000, crossing.Lines[0].AmountToman);
        Assert.Equal(RateSource.Holiday, crossing.Lines[1].Source);
        Assert.Equal(50_000, crossing.Lines[1].AmountToman);
        Assert.Equal(150_000, crossing.TotalAmountToman);
    }

    [Fact]
    public void Session_longer_than_a_day_is_split_across_days_and_bands()
    {
        // Tuesday 22:00 → Thursday 04:00 is 30 hours.
        var charge = CafeFixture.Price(CafeFixture.Session(
            "long",
            CafeFixture.Play("a", T.At(2026, 6, 2, 22, 0), T.At(2026, 6, 4, 4, 0), "ps5-1", CafeFixture.Ps5, 1)));

        Assert.Equal(30 * 3_600, charge.TotalActiveSeconds);
        Assert.Equal(30 * 60, charge.TotalBilledMinutes);
        Assert.Equal(4, charge.Lines.Count);
        Assert.Equal(220_000, charge.Lines[0].AmountToman);
        Assert.Equal(1_360_000, charge.Lines[1].AmountToman);
        Assert.Equal(1_705_000, charge.Lines[2].AmountToman);
        Assert.Equal(640_000, charge.Lines[3].AmountToman);
        Assert.Equal(3_925_000, charge.TotalAmountToman);
        Assert.Equal(charge.Lines.Sum(line => line.BilledMinutes), charge.TotalBilledMinutes);
        Assert.Equal(charge.Lines.Sum(line => line.ActiveSeconds), charge.TotalActiveSeconds);
    }

    [Fact]
    public void Online_rate_ignores_player_count_and_vr_is_not_invented()
    {
        var day = CafeFixture.Price(CafeFixture.Session(
            "on",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0), "ps5-1", CafeFixture.Ps5, 4, PlayMode.Online)));
        Assert.Equal(170_000, day.TotalAmountToman);

        var night = CafeFixture.Price(CafeFixture.Session(
            "on-night",
            CafeFixture.Play("a", T.At(2026, 6, 2, 1, 0), T.At(2026, 6, 2, 2, 0), "ps5-1", CafeFixture.Ps5, 1, PlayMode.Online)));
        Assert.Equal(220_000, night.TotalAmountToman);
        Assert.Equal(RateSource.TimeBand, night.Lines[0].Source);

        var error = Assert.Throws<BillingException>(() => CafeFixture.Price(CafeFixture.Session(
            "vr",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0), "ps5-1", CafeFixture.Ps5, 1, PlayMode.Vr))));
        Assert.Equal("tariff_not_found", error.Code);
    }

    [Fact]
    public void Overlapping_segments_and_a_bad_offset_are_rejected()
    {
        var overlap = Assert.Throws<BillingException>(() => CafeFixture.Price(CafeFixture.Session(
            "bad",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0), "ps5-1", CafeFixture.Ps5, 1),
            CafeFixture.Play("b", T.At(2026, 6, 2, 10, 30), T.At(2026, 6, 2, 11, 30), "ps5-1", CafeFixture.Ps5, 1))));
        Assert.Equal("invalid_session", overlap.Code);

        var offset = Assert.Throws<BillingException>(() => CafeFixture.Price(CafeFixture.Session(
            "utc",
            new PlaySegment(
                "a",
                new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 6, 2, 11, 0, 0, TimeSpan.Zero),
                "ps5-1",
                CafeFixture.Ps5,
                PlayMode.Offline,
                1))));
        Assert.Equal("invalid_time", offset.Code);
    }

    private static SessionCharge Price(RateCard card, HolidayCalendar holidays, DateTimeOffset start, DateTimeOffset end, int players, PlayMode mode = PlayMode.Offline)
    {
        return SessionPricer.Price(
            CafeFixture.Session("s", CafeFixture.Play("a", start, end, "ps5-1", CafeFixture.Ps5, players, mode)),
            card,
            holidays);
    }
}
