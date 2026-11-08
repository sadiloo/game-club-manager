using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class ReviewFixesTests
{
    [Fact]
    public void Three_pauses_in_ten_minutes_of_play_bill_ten_minutes()
    {
        var start = T.At(2026, 6, 2, 10, 0);
        var end = TehranTime.AddSeconds(start, 780);
        var charge = CafeFixture.Price(new Session(
            "paused",
            [CafeFixture.Play("a", start, end, "ps5-1", CafeFixture.Ps5, 1)],
            [
                new PauseInterval(TehranTime.AddSeconds(start, 150), TehranTime.AddSeconds(start, 210)),
                new PauseInterval(TehranTime.AddSeconds(start, 360), TehranTime.AddSeconds(start, 420)),
                new PauseInterval(TehranTime.AddSeconds(start, 570), TehranTime.AddSeconds(start, 630)),
            ]));

        Assert.Equal(600, charge.TotalActiveSeconds);
        Assert.Equal(10, charge.TotalBilledMinutes);
        Assert.Equal(18_333, Assert.Single(charge.Lines).AmountToman);
        Assert.Equal(charge.Lines.Sum(line => line.AmountToman), charge.TotalAmountToman);
        foreach (var line in charge.Lines)
        {
            Assert.Equal(MoneyMath.ChargeForMinutes(line.HourlyRateToman, line.BilledMinutes, RateRemainderMode.Nearest), line.AmountToman);
        }
    }

    [Fact]
    public void A_minute_that_crosses_0830_or_midnight_keeps_the_starting_rate()
    {
        var acrossBand = CafeFixture.Price(CafeFixture.Session(
            "band",
            CafeFixture.Play("a", T.At(2026, 6, 2, 8, 29, 30), T.At(2026, 6, 2, 8, 30, 30), "ps5-1", CafeFixture.Ps5, 1)));
        Assert.Equal(1, acrossBand.TotalBilledMinutes);
        Assert.Equal(RateSource.TimeBand, Assert.Single(acrossBand.Lines).Source);
        Assert.Equal(MoneyMath.ChargeForMinutes(160_000, 1, RateRemainderMode.Nearest), acrossBand.TotalAmountToman);

        var twoMinutes = CafeFixture.Price(CafeFixture.Session(
            "two",
            CafeFixture.Play("a", T.At(2026, 6, 2, 8, 29), T.At(2026, 6, 2, 8, 31), "ps5-1", CafeFixture.Ps5, 1)));
        Assert.Equal(2, twoMinutes.TotalBilledMinutes);
        Assert.Equal(RateSource.TimeBand, twoMinutes.Lines[0].Source);
        Assert.Equal(RateSource.BaseTariff, twoMinutes.Lines[1].Source);

        var acrossMidnight = CafeFixture.Price(CafeFixture.Session(
            "mid",
            CafeFixture.Play("a", T.At(2026, 6, 2, 23, 59, 30), T.At(2026, 6, 3, 0, 0, 30), "ps5-1", CafeFixture.Ps5, 1)));
        Assert.Equal(1, acrossMidnight.TotalBilledMinutes);
        Assert.Equal(RateSource.BaseTariff, Assert.Single(acrossMidnight.Lines).Source);
    }

    [Fact]
    public void A_midnight_wrapping_band_owns_the_minute_that_starts_inside_it()
    {
        var card = new RateCardBuilder()
            .AddBand(new TimeBand("wrap", CafeFixture.Ps5, "Late", new TimeOfDay(22, 0), new TimeOfDay(2, 0)))
            .AddBase(CafeFixture.Ps5, PlayMode.Offline, 1, 110_000)
            .AddBandRate("wrap", CafeFixture.Ps5, PlayMode.Offline, 1, 160_000)
            .Build();
        var inside = SessionPricer.Price(
            CafeFixture.Session("in", CafeFixture.Play("a", T.At(2026, 6, 2, 23, 30), T.At(2026, 6, 3, 0, 30), "ps5-1", CafeFixture.Ps5, 1)),
            card);
        var insideLine = Assert.Single(inside.Lines);
        Assert.Equal(RateSource.TimeBand, insideLine.Source);
        Assert.Equal(160_000, inside.TotalAmountToman);

        var leaving = SessionPricer.Price(
            CafeFixture.Session("out", CafeFixture.Play("a", T.At(2026, 6, 3, 1, 59), T.At(2026, 6, 3, 2, 1), "ps5-1", CafeFixture.Ps5, 1)),
            card);
        Assert.Equal(2, leaving.TotalBilledMinutes);
        Assert.Equal(RateSource.TimeBand, leaving.Lines[0].Source);
        Assert.Equal(RateSource.BaseTariff, leaving.Lines[1].Source);
    }

    [Fact]
    public void Wallet_debt_is_refused_unless_the_branch_allows_it()
    {
        var refused = PaymentSplitter.Split(80_000, [
            new Tender(PaymentMethod.Wallet, 80_000, "m"),
        ], [new MemberWallet("m", 30_000, 0)]);
        Assert.False(refused.IsValid);
        Assert.Contains(refused.Errors, error => error.Code == PaymentErrors.InsufficientWallet);
        Assert.Equal(30_000, refused.RemainingWallets.Single().BalanceToman);

        var account = new CustomerAccount(
            "m",
            WalletSettings.Shared,
            new WalletPocket(30_000, 0),
            new WalletPocket(0, 0),
            []);
        var charge = account.ChargeNonPc(80_000);
        Assert.False(charge.Accepted);
        Assert.Equal(30_000, charge.Account.PcMoneyToman);

        var separate = account with { Settings = WalletSettings.Separate };
        Assert.False(separate.ChargeNonPc(80_000).Accepted);
        Assert.Equal(0, separate.NonPcMoneyToman);
    }

    [Fact]
    public void Many_small_gift_draws_match_one_large_draw()
    {
        var stepByStep = new WalletPocket(2_000, 1_000);
        long gift = 0;
        long real = 0;
        for (var i = 0; i < 3; i++)
        {
            Assert.True(stepByStep.TrySpend(1_000, DebtPolicy.None, out stepByStep, out var g, out var r));
            gift += g;
            real += r;
        }

        var once = new WalletPocket(2_000, 1_000);
        Assert.True(once.TrySpend(3_000, DebtPolicy.None, out var after, out var giftOnce, out var realOnce));
        Assert.Equal(giftOnce, gift);
        Assert.Equal(realOnce, real);
        Assert.Equal(after.GiftToman, stepByStep.GiftToman);
        Assert.Equal(after.RealToman, stepByStep.RealToman);
        Assert.Equal(1_000, gift);
        Assert.Equal(2_000, real);

        var debt = new WalletPocket(50, 50);
        Assert.True(debt.TrySpend(120, DebtPolicy.Unlimited, out var indebted, out var debtGift, out var debtReal));
        Assert.Equal(50, debtGift);
        Assert.Equal(70, debtReal);
        Assert.Equal(0, indebted.GiftToman);
        Assert.Equal(-20, indebted.RealToman);
    }

    [Fact]
    public void Pc_invoice_payable_equals_the_wallet_debit_with_grace_and_a_level()
    {
        var minute = MoneyMath.ChargeForMinutes(110_000, 1, RateRemainderMode.Nearest);
        var charge = OneMinute();
        var plain = CustomerPlay.Settle(charge, PcWallet(500, false));
        Assert.Equal(0, plain.Settlement.MoneyConsumedToman);
        Assert.Equal(minute, plain.Settlement.Grace!.WaivedToman);
        Assert.Equal(500, plain.Account.PcMoneyToman);
        Assert.NotNull(plain.ChargeInvoice);
        Assert.Equal(0, plain.ChargeInvoice!.PayableToman);

        var discountedAccount = PcWallet(500, true);
        var titan = new CustomerPricing("Titan", 28, null, 0);
        var discounted = CustomerPlay.Settle(charge, discountedAccount, pricing: titan);
        var netMinute = new PcTimeDiscount(28, 0, 0).Net(minute);
        Assert.Equal(0, discounted.ChargeInvoice!.PayableToman);
        Assert.Equal(minute - netMinute, discounted.ChargeInvoice.DiscountToman);
        Assert.Equal(0, discounted.Settlement.MoneyConsumedToman);
        Assert.Equal(netMinute, discounted.Settlement.Grace!.WaivedToman);
        Assert.Equal(500, discounted.Account.PcMoneyToman);
        Assert.Equal(0, discounted.ChargeInvoice.PayableToman);

        var ignored = CustomerPlay.Settle(charge, PcWallet(500, false), pricing: titan);
        Assert.Equal(0, ignored.Settlement.MoneyConsumedToman);
        Assert.Equal(500, ignored.Account.PcMoneyToman);
        Assert.Equal(0, ignored.ChargeInvoice!.PayableToman);
    }

    [Fact]
    public void Apply_discounts_to_pc_lengthens_the_remaining_time_estimate()
    {
        var at = T.At(2026, 6, 2, 10, 0);
        var pricing = new CustomerPricing("Titan", 28, null, 0);
        var off = CustomerPlay.Estimate(
            PcWallet(3_667, false),
            CafeFixture.ExampleNormal,
            PlayMode.Offline,
            1,
            at,
            CafeFixture.Card(),
            pricing: pricing);
        var on = CustomerPlay.Estimate(
            PcWallet(3_667, true),
            CafeFixture.ExampleNormal,
            PlayMode.Offline,
            1,
            at,
            CafeFixture.Card(),
            pricing: pricing);
        Assert.Equal(2, off.MoneyMinutes);
        Assert.Equal(3, on.MoneyMinutes);

        var grace = CustomerPlay.Estimate(
            PcWallet(500, true),
            CafeFixture.ExampleNormal,
            PlayMode.Offline,
            1,
            at,
            CafeFixture.Card(),
            pricing: pricing);
        Assert.Equal(1, grace.MoneyMinutes);
    }

    [Fact]
    public void Grace_is_one_minute_under_coarse_blocks_and_with_an_empty_windowed_package()
    {
        var minute = MoneyMath.ChargeForMinutes(110_000, 1, RateRemainderMode.Nearest);
        foreach (var granularity in new[] { 5, 15 })
        {
            var policy = new BillingPolicy(granularity, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
            var charge = CafeFixture.Price(CafeFixture.Session(
                "s",
                CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 10, 0, 1), "example-pc-1", CafeFixture.ExampleNormal, 1)), policy);
            Assert.Equal(granularity, charge.TotalBilledMinutes);
            var settled = AccountSettlement.Settle(charge, new PcAccount("m", 500, []), billing: policy);
            Assert.Equal(1, settled.Grace!.Minutes);
            Assert.Equal(minute, settled.Grace.WaivedToman);
            Assert.Equal(0, settled.MoneyConsumedToman);
            Assert.Equal(500, settled.UpdatedAccount.MoneyBalanceToman);
            Assert.Equal(0, settled.UncoveredToman);
            Assert.Equal(0, settled.WriteOffToman);
            Assert.Null(settled.Alert);
            Assert.Equal(charge.TotalAmountToman - minute, settled.OmittedToman);
            Assert.Equal(charge.TotalAmountToman, settled.PackageCoveredToman + settled.MoneyConsumedToman + settled.Grace.WaivedToman + settled.WriteOffToman + settled.UncoveredToman + settled.OmittedToman);
        }

        var emptyWindow = new TimePackage(
            "empty",
            0,
            null,
            null,
            new TimeOfDay(10, 0),
            new TimeOfDay(10, 2));
        var split = CafeFixture.Price(CafeFixture.Session(
            "s",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 10, 2), "example-pc-1", CafeFixture.ExampleNormal, 1)));
        var withPackage = AccountSettlement.Settle(split, new PcAccount("m", 500, [emptyWindow]));
        Assert.NotNull(withPackage.Grace);
        Assert.Equal(1, withPackage.Grace!.Minutes);
        Assert.Equal(500, withPackage.MoneyConsumedToman);
    }

    [Fact]
    public void A_free_rate_does_not_walk_the_year_and_overflow_is_a_billing_exception()
    {
        var card = new RateCardBuilder()
            .AddBase(CafeFixture.ExampleNormal, PlayMode.Offline, 1, 0)
            .Build();
        var plan = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 10, 0),
            1_000_000,
            "pre",
            "example-pc-1",
            CafeFixture.ExampleNormal,
            PlayMode.Offline,
            1,
            card);
        Assert.True(plan.IsUnlimited);
        Assert.Equal(plan.Start, plan.End);
        Assert.Equal(0, plan.ConsumedToman);
        Assert.Equal(1_000_000, plan.UnusedToman);
        Assert.Empty(plan.Lines);

        var overflow = Assert.Throws<BillingException>(() => MoneyMath.ChargeForMinutes(long.MaxValue, 120, RateRemainderMode.Floor));
        Assert.Equal(BillingErrors.InvalidMoney, overflow.Code);
        var budget = Assert.Throws<BillingException>(() => MoneyMath.MaxAffordableMinutes(1, long.MaxValue, BillingPolicy.Default));
        Assert.Equal(BillingErrors.InvalidMoney, budget.Code);
        var invoice = Assert.Throws<BillingException>(() => InvoiceCalculator.Calculate(new InvoiceRequest(
            [
                new TimeChargeLine("a", long.MaxValue, null, null, null),
                new TimeChargeLine("b", long.MaxValue, null, null, null),
            ],
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            RoundingPolicy.Off,
            T.At(2026, 6, 2, 10, 0))));
        Assert.Equal(BillingErrors.InvalidMoney, invoice.Code);
    }

    [Fact]
    public void Odd_rounding_steps_and_tehran_normalization_are_explicit()
    {
        var (up, upDelta) = MoneyMath.Round(10, RoundingPolicy.Create(3, RoundingDirection.Up));
        Assert.Equal(12, up);
        Assert.Equal(2, upDelta);
        var (down, downDelta) = MoneyMath.Round(10, RoundingPolicy.Create(3, RoundingDirection.Down));
        Assert.Equal(9, down);
        Assert.Equal(-1, downDelta);

        var fromUtc = TehranTime.FromUtc(new DateTime(2026, 6, 2, 6, 30, 0, DateTimeKind.Utc));
        Assert.Equal(T.At(2026, 6, 2, 10, 0), fromUtc);

        var win7 = new DateTimeOffset(2026, 6, 2, 10, 0, 0, new TimeSpan(4, 30, 0));
        var normalized = TehranTime.Ensure(win7);
        Assert.Equal(new TimeSpan(3, 30, 0), normalized.Offset);
        Assert.Equal(win7.UtcDateTime, normalized.UtcDateTime);
        Assert.Equal(9, normalized.Hour);
        Assert.Equal(0, normalized.Minute);
    }

    [Fact]
    public void Esfand_30_follows_the_leap_years_and_days_around_cross_nowruz()
    {
        Assert.True(JalaliCalendar.IsLeapYear(1399));
        Assert.True(JalaliCalendar.IsLeapYear(1403));
        Assert.True(JalaliCalendar.IsLeapYear(1408));
        Assert.False(JalaliCalendar.IsLeapYear(1404));
        Assert.False(JalaliCalendar.IsLeapYear(1407));
        Assert.Throws<BillingException>(() => new JalaliDate(1404, 12, 30));
        Assert.Throws<BillingException>(() => SpecialDay.EveryYear(7, 31, "bad", "bad"));
        Assert.Throws<BillingException>(() => SpecialDay.EveryYear(12, 31, "bad", "bad"));
        _ = SpecialDay.EveryYear(12, 30, "Esfand 30", "e30");

        var card = new RateCardBuilder()
            .AddBase(CafeFixture.Ps5, PlayMode.Offline, 1, 110_000)
            .AddSpecialRate("e30", CafeFixture.Ps5, PlayMode.Offline, 1, 50_000)
            .Build();
        var days = new SpecialDayCalendar([SpecialDay.EveryYear(12, 30, "Esfand 30", "e30")]);
        Assert.Equal(50_000, Hour(card, days, new JalaliDate(1403, 12, 30)));
        Assert.Equal(50_000, Hour(card, days, new JalaliDate(1404, 12, 29)));
        Assert.Equal(110_000, Hour(card, days, new JalaliDate(1404, 12, 28)));

        var window = BirthdayWindow.DaysAround(3);
        var birth = new JalaliDate(1380, 1, 1);
        Assert.True(window.Includes(birth, new JalaliDate(1405, 1, 1).ToGregorian()));
        Assert.True(window.Includes(birth, new JalaliDate(1404, 12, 29).ToGregorian()));
        Assert.False(window.Includes(birth, new JalaliDate(1404, 12, 28).ToGregorian()));
        Assert.True(window.Includes(birth, new JalaliDate(1405, 1, 2).ToGregorian()));
        Assert.False(window.Includes(birth, new JalaliDate(1405, 1, 3).ToGregorian()));
        Assert.Throws<BillingException>(() => default(JalaliDate).ToGregorian());
        Assert.Throws<BillingException>(() => DiscountCode.PercentOff("ZERO", 0));
    }

    [Fact]
    public void A_package_stops_at_midnight_and_prepaid_matches_settlement()
    {
        var package = new TimePackage("day", 1_000, null, null, null, null, new LocalDate(2026, 6, 2));
        var account = new PcAccount("m", 1_000_000, [package]);
        var charge = CafeFixture.Price(CafeFixture.Session(
            "night",
            CafeFixture.Play("a", T.At(2026, 6, 2, 23, 0), T.At(2026, 6, 3, 1, 0), "example-pc-1", CafeFixture.ExampleNormal, 1)));
        Assert.Single(charge.Lines);
        var settled = AccountSettlement.Settle(charge, account);
        Assert.Equal(60, settled.PackageMinutesConsumed);
        Assert.Equal(110_000, settled.MoneyConsumedToman);
        Assert.Equal(0, settled.UncoveredToman);

        var plan = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 10, 0),
            110_000,
            "pre",
            "example-pc-1",
            CafeFixture.ExampleNormal,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        var played = CafeFixture.Price(CafeFixture.Session(
            "played",
            CafeFixture.Play("a", plan.Start, plan.End, "example-pc-1", CafeFixture.ExampleNormal, 1)));
        var paid = AccountSettlement.Settle(played, new PcAccount("m", 110_000, []));
        Assert.Equal(plan.ConsumedToman, paid.MoneyConsumedToman);
        Assert.Null(plan.Grace);
        Assert.Null(paid.Grace);

        var small = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 10, 0),
            500,
            "pre",
            "example-pc-1",
            CafeFixture.ExampleNormal,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        var smallPlayed = CafeFixture.Price(CafeFixture.Session(
            "small",
            CafeFixture.Play("a", small.Start, small.End, "example-pc-1", CafeFixture.ExampleNormal, 1)));
        var smallPaid = AccountSettlement.Settle(smallPlayed, new PcAccount("m", 500, []));
        Assert.Equal(small.Grace!.WaivedToman, smallPaid.Grace!.WaivedToman);
        Assert.Equal(0, smallPaid.MoneyConsumedToman);
        Assert.Equal(500, smallPaid.UpdatedAccount.MoneyBalanceToman);
    }

    [Fact]
    public void Invoice_lines_carry_a_date_and_a_persian_label()
    {
        var invoice = InvoiceCalculator.Calculate(new InvoiceRequest(
            [new TimeChargeLine("time", 10_000, CafeFixture.Ps5, new LocalDate(2026, 6, 2), DayOfWeek.Tuesday)],
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            RoundingPolicy.Off,
            T.At(2026, 6, 2, 18, 0)));
        var line = Assert.Single(invoice.Lines);
        Assert.Equal(new LocalDate(2026, 6, 2), line.ServiceDate);
        Assert.Equal("زمان", line.PersianLabel);
    }

    [Fact]
    public void A_line_is_rounded_once_and_the_invoice_step_is_applied_once_to_the_total()
    {
        Assert.Equal(110_000, MoneyMath.ChargeForMinutes(110_000, 60, RateRemainderMode.Nearest));
        Assert.Equal(109_980, 60 * MoneyMath.ChargeForMinutes(110_000, 1, RateRemainderMode.Nearest));
        Assert.Equal(3_667, MoneyMath.ChargeForMinutes(110_000, 2, RateRemainderMode.Nearest));

        var hour = CafeFixture.Price(CafeFixture.Session(
            "hour",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0), "ps5-1", CafeFixture.Ps5, 1)));
        Assert.Equal(110_000, Assert.Single(hour.Lines).AmountToman);

        var twoMinutes = CafeFixture.Price(CafeFixture.Session(
            "two",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 10, 2), "ps5-1", CafeFixture.Ps5, 1)));
        Assert.Equal(3_667, twoMinutes.TotalAmountToman);

        var invoice = InvoiceCalculator.Calculate(new InvoiceRequest(
            [
                new TimeChargeLine("a", 50_040, CafeFixture.Ps5, new LocalDate(2026, 6, 2), DayOfWeek.Tuesday),
                new TimeChargeLine("b", 50_040, CafeFixture.Ps5, new LocalDate(2026, 6, 2), DayOfWeek.Tuesday),
            ],
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            RoundingPolicy.Create(100, RoundingDirection.Up),
            T.At(2026, 6, 2, 18, 0)));
        Assert.Equal(100_100, invoice.PayableToman);
        Assert.Equal(20, invoice.RoundingToman);
    }

    [Fact]
    public void A_special_day_without_a_player_count_falls_through_and_a_missing_tariff_still_throws()
    {
        var card = new RateCardBuilder()
            .AddBand(GameTimerReference.SpecialHours)
            .AddBase(CafeFixture.Ps5, PlayMode.Offline, 1, 110_000)
            .AddBase(CafeFixture.Ps5, PlayMode.Offline, 2, 125_000)
            .AddBandRate(GameTimerReference.SpecialBandId, CafeFixture.Ps5, PlayMode.Offline, 2, 175_000)
            .AddWeekday(DayOfWeek.Saturday, CafeFixture.Ps5, PlayMode.Offline, 2, 90_000)
            .AddSpecialRate("festival", CafeFixture.Ps5, PlayMode.Offline, 1, 50_000)
            .Build();
        var days = new SpecialDayCalendar(
            [SpecialDay.Once(JalaliDate.FromGregorian(new LocalDate(2026, 6, 6)), "Festival", "festival")]);

        var onePlayer = SessionPricer.Price(
            CafeFixture.Session("one", CafeFixture.Play("a", T.At(2026, 6, 6, 10, 0), T.At(2026, 6, 6, 11, 0), "ps5-1", CafeFixture.Ps5, 1)),
            card,
            days);
        Assert.Equal(RateSource.SpecialDay, Assert.Single(onePlayer.Lines).Source);
        Assert.Equal(50_000, onePlayer.TotalAmountToman);

        var twoAtNight = SessionPricer.Price(
            CafeFixture.Session("night", CafeFixture.Play("a", T.At(2026, 6, 6, 1, 0), T.At(2026, 6, 6, 2, 0), "ps5-1", CafeFixture.Ps5, 2)),
            card,
            days);
        Assert.Equal(RateSource.TimeBand, Assert.Single(twoAtNight.Lines).Source);
        Assert.Equal(175_000, twoAtNight.TotalAmountToman);

        var twoByDay = SessionPricer.Price(
            CafeFixture.Session("day", CafeFixture.Play("a", T.At(2026, 6, 6, 10, 0), T.At(2026, 6, 6, 11, 0), "ps5-1", CafeFixture.Ps5, 2)),
            card,
            days);
        Assert.Equal(RateSource.Weekday, Assert.Single(twoByDay.Lines).Source);
        Assert.Equal(90_000, twoByDay.TotalAmountToman);

        var nowhere = new RateCardBuilder()
            .AddSpecialRate("festival", CafeFixture.Ps5, PlayMode.Offline, 1, 50_000)
            .Build();
        var error = Assert.Throws<BillingException>(() => SessionPricer.Price(
            CafeFixture.Session("missing", CafeFixture.Play("a", T.At(2026, 6, 6, 10, 0), T.At(2026, 6, 6, 11, 0), "ps5-1", CafeFixture.Ps5, 2)),
            nowhere,
            days));
        Assert.Equal(BillingErrors.TariffNotFound, error.Code);
    }

    private static SessionCharge OneMinute()
    {
        return CafeFixture.Price(CafeFixture.Session(
            "pc",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 10, 1), "example-pc-1", CafeFixture.ExampleNormal, 1)));
    }

    private static CustomerAccount PcWallet(long balance, bool applyDiscounts)
    {
        return new CustomerAccount(
            "m",
            WalletSettings.Shared with { ApplyDiscountsToPc = applyDiscounts },
            new WalletPocket(balance, 0),
            new WalletPocket(0, 0),
            []);
    }

    private static long Hour(RateCard card, SpecialDayCalendar days, JalaliDate day)
    {
        var date = day.ToGregorian();
        var start = TehranTime.At(date.Year, date.Month, date.Day, 12, 0);
        return SessionPricer.Price(
            CafeFixture.Session("s", CafeFixture.Play("a", start, start.AddHours(1), "ps5-1", CafeFixture.Ps5, 1)),
            card,
            days).TotalAmountToman;
    }
}
