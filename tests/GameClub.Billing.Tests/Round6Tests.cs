using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class Round6Tests
{
    [Fact]
    public void Flat_block_grace_across_midnight_is_not_overshoot()
    {
        var card = Flat();
        var start = T.At(2026, 6, 2, 23, 50);
        var five = new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var plan5 = PrepaidPlanner.ForAmount(start, 18_833, "a", "st", "g", PlayMode.Offline, 1, card, policy: five);
        Assert.Equal(T.At(2026, 6, 3, 0, 1), plan5.End);
        Assert.Equal(500, plan5.UnusedToman);
        Assert.NotNull(plan5.Grace);
        var played5 = Price(card, start, plan5.End, five);
        var settled5 = AccountSettlement.Settle(played5, new PcAccount("m", 18_833, []), billing: five);
        Assert.Equal(plan5.ConsumedToman, settled5.MoneyConsumedToman);
        Assert.Equal(plan5.Grace!.WaivedToman, settled5.Grace!.WaivedToman);
        Assert.Equal(500, settled5.UpdatedAccount.MoneyBalanceToman);
        Assert.Equal(0, settled5.WriteOffToman);
        Assert.Null(settled5.Alert);

        var fifteen = new BillingPolicy(15, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var plan15 = PrepaidPlanner.ForAmount(start, 55_500, "a", "st", "g", PlayMode.Offline, 1, card, policy: fifteen);
        Assert.Equal(500, plan15.UnusedToman);
        Assert.NotNull(plan15.Grace);
        var settled15 = AccountSettlement.Settle(Price(card, start, plan15.End, fifteen), new PcAccount("m", 55_500, []), billing: fifteen);
        Assert.Equal(0, settled15.WriteOffToman);
        Assert.Null(settled15.Alert);
        Assert.Equal(500, settled15.UpdatedAccount.MoneyBalanceToman);
        Assert.Equal(plan15.Grace!.WaivedToman, settled15.Grace!.WaivedToman);
    }

    [Fact]
    public void A_code_window_edge_mid_session_discounts_only_the_minutes_inside_it()
    {
        var card = Flat();
        var half = DiscountCode.PercentOff("HALF", 50);
        var untilEleven = half with { ValidUntil = T.At(2026, 6, 2, 11, 0) };
        var fromEleven = half with { ValidFrom = T.At(2026, 6, 2, 11, 0) };
        var twoHours = Play(card, T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 12, 0));
        Assert.Equal(55_000, CustomerPlay.Settle(twoHours, Wallet(), code: untilEleven).Settlement.DiscountToman);
        Assert.Equal(55_000, CustomerPlay.Settle(twoHours, Wallet(), code: fromEleven).Settlement.DiscountToman);

        var until2330 = half with { ValidUntil = T.At(2026, 6, 2, 23, 30) };
        var from1030 = half with { ValidFrom = T.At(2026, 6, 2, 10, 30) };
        var night = Play(card, T.At(2026, 6, 2, 23, 0), T.At(2026, 6, 3, 1, 0));
        var morning = Play(card, T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0));
        Assert.Equal(27_500, CustomerPlay.Settle(night, Wallet(), code: until2330, issuedAt: T.At(2026, 6, 3, 1, 0)).Settlement.DiscountToman);
        Assert.Equal(27_500, CustomerPlay.Settle(morning, Wallet(), code: from1030).Settlement.DiscountToman);
    }

    [Fact]
    public void Grace_across_midnight_uses_the_whole_run_marginal()
    {
        var card = Flat();
        var start = T.At(2026, 6, 2, 23, 55);
        foreach (var budget in new long[] { 12_834, 18_334, 23_834 })
        {
            var plan = PrepaidPlanner.ForAmount(start, budget, "a", "st", "g", PlayMode.Offline, 1, card);
            var settled = AccountSettlement.Settle(Price(card, start, plan.End, null), new PcAccount("m", budget, []));
            Assert.Equal(plan.ConsumedToman, settled.MoneyConsumedToman);
            Assert.Equal(plan.Grace?.WaivedToman ?? 0, settled.Grace?.WaivedToman ?? 0);
            Assert.NotNull(settled.Grace);
            Assert.Equal(0, settled.WriteOffToman);
            Assert.Null(settled.Alert);
            Assert.Equal(0, settled.UncoveredToman);
        }
    }

    [Fact]
    public void A_kind_conflict_is_rejected_with_no_pause()
    {
        var stations = new StationDirectory(
            [new StationGroup(PilotSeed.PcNormalGroupId, "PC", StationKind.Pc)],
            [new Station("pc1", "PC 1", PilotSeed.PcNormalGroupId)]);
        var start = T.At(2026, 6, 2, 10, 0);
        var session = new Session("pc", [
            new PlaySegment("a", start, start.AddMinutes(10), "pc1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, StationKind.Console),
        ]);
        var error = Assert.Throws<BillingException>(() => SessionPricer.Price(session, PilotSeed.CreateCard(), stations: stations));
        Assert.Equal(BillingErrors.InvalidSession, error.Code);
    }

    [Fact]
    public void A_station_missing_from_the_directory_is_invalid_session()
    {
        var stations = new StationDirectory(
            [new StationGroup("pcg", "PC", StationKind.Pc)],
            [new Station("pc-1", "PC 1", "pcg")]);
        var start = T.At(2026, 6, 2, 10, 0);
        var session = new Session("u", [
            new PlaySegment("a", start, start.AddMinutes(10), "zz", "pcg", PlayMode.Offline, 1),
        ]);
        var error = Assert.Throws<BillingException>(() => SessionPricer.Price(session, Flat(), stations: stations));
        Assert.Equal(BillingErrors.InvalidSession, error.Code);
    }

    [Fact]
    public void Gap_line_saturday_code_50_percent_gives_55000()
    {
        var charge = GapLineCharge();
        var spec = new SessionDiscount(CodePercent: 50, CodeWeekdays: [DayOfWeek.Saturday]);
        Assert.Equal(55_000, Discount(charge, spec));
    }

    [Fact]
    public void Gap_line_saturday_birthday_20_percent_gives_22000()
    {
        var charge = GapLineCharge();
        var birth = JalaliDate.FromGregorian(new LocalDate(2026, 6, 6));
        var spec = new SessionDiscount(BirthdayPercent: 20, BirthDate: birth);
        Assert.Equal(22_000, Discount(charge, spec));
    }

    [Fact]
    public void Gap_line_code_valid_until_fri_2330_gives_55000()
    {
        var charge = GapLineCharge();
        var spec = new SessionDiscount(CodePercent: 50, ValidUntil: T.At(2026, 6, 5, 23, 30));
        Assert.Equal(55_000, Discount(charge, spec));
    }

    [Fact]
    public void Gap_line_code_valid_from_sat_0030_gives_55000()
    {
        var charge = GapLineCharge();
        var spec = new SessionDiscount(CodePercent: 50, ValidFrom: T.At(2026, 6, 6, 0, 30));
        Assert.Equal(55_000, Discount(charge, spec));
    }

    [Fact]
    public void Gap_line_block_grace_minute_is_at_sat_0040()
    {
        var charge = SessionPricer.Price(
            GapSession(),
            Flat(),
            policy: new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest));
        var billing = new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var balance = OracleHalfUp(110_000, 70) + 10;
        var settled = AccountSettlement.Settle(charge, new PcAccount("m", balance, []), billing: billing);
        Assert.Equal(T.At(2026, 6, 6, 0, 40), settled.Grace!.At);
    }

    [Fact]
    public void Console_pause_code_valid_from_1050_gives_36666()
    {
        var charge = ConsolePauseCharge();
        var spec = new SessionDiscount(CodePercent: 50, ValidFrom: T.At(2026, 6, 2, 10, 50));
        Assert.Equal(36_666, Discount(charge, spec));
    }

    [Fact]
    public void Console_pause_code_valid_until_1030_gives_13750()
    {
        var charge = ConsolePauseCharge();
        var spec = new SessionDiscount(CodePercent: 50, ValidUntil: T.At(2026, 6, 2, 10, 30));
        Assert.Equal(13_750, Discount(charge, spec));
    }

    [Fact]
    public void Fixed_code_opens_mid_line_level_28_and_fixed_5000_gives_74200()
    {
        var start = T.At(2026, 6, 2, 10, 0);
        var charge = Play(Flat(), start, start.AddHours(1));
        var code = new DiscountCode("FX", 0, 5_000, start.AddMinutes(30), null, null, null);
        var result = CustomerPlay.Settle(charge, Wallet(), pricing: new CustomerPricing("Titan", 28, null, 0), code: code, issuedAt: start.AddHours(1));
        Assert.Equal(74_200, result.Settlement.MoneyConsumedToman);
        Assert.Equal(result.Settlement.MoneyConsumedToman, result.ChargeInvoice!.PayableToman);
    }

    [Fact]
    public void Fixed_code_opens_mid_line_level_28_code_10_and_fixed_5000_gives_68700()
    {
        var start = T.At(2026, 6, 2, 10, 0);
        var charge = Play(Flat(), start, start.AddHours(1));
        var code = new DiscountCode("FX", 10, 5_000, start.AddMinutes(30), null, null, null);
        var result = CustomerPlay.Settle(
            charge,
            Wallet(),
            pricing: new CustomerPricing("Titan", 28, null, 0),
            code: code,
            issuedAt: start.AddHours(1));
        Assert.Equal(68_700, result.Settlement.MoneyConsumedToman);
        Assert.Equal(result.Settlement.MoneyConsumedToman, result.ChargeInvoice!.PayableToman);
    }

    [Fact]
    public void Row_93_level_30_and_code_80_from_mid_hour_gives_38500_payable()
    {
        var start = T.At(2026, 6, 2, 10, 0);
        var charge = Play(Flat(), start, start.AddHours(1));
        var spec = new SessionDiscount(LevelPercent: 30, CodePercent: 80, ValidFrom: start.AddMinutes(30));
        var settled = AccountSettlement.Settle(charge, new PcAccount("m", 100_000_000, []), discount: spec);
        Assert.Equal(38_500, settled.MoneyConsumedToman);
        Assert.Equal(71_500, settled.DiscountToman);
        var play = CustomerPlay.Settle(
            charge,
            Wallet(),
            pricing: new CustomerPricing("L", 30, null, 0),
            code: new DiscountCode("P", 80, 0, start.AddMinutes(30), null, null, null),
            issuedAt: start.AddHours(1));
        Assert.Equal(38_500, play.Settlement.MoneyConsumedToman);
        Assert.Equal(play.Settlement.MoneyConsumedToman, play.ChargeInvoice!.PayableToman);
    }

    [Fact]
    public void Settlement_matches_an_independent_per_minute_oracle()
    {
        var rng = new Random(606);
        var flat = Flat();
        var pilot = PilotSeed.CreateCard();
        var cards = new (RateCard Card, string GroupId)[]
        {
            (pilot, PilotSeed.PcVipGroupId),
            (pilot, PilotSeed.PcNormalGroupId),
            (flat, "g"),
        };
        var granularities = new[] { 1, 5, 15 };
        var balances = new long[] { 5_000, 50_000, 500_000, 5_000_000, 1_000_000_000 };

        for (var i = 0; i < 500; i++)
        {
            var (card, groupId) = cards[rng.Next(cards.Length)];
            var policy = new BillingPolicy(
                granularities[rng.Next(granularities.Length)],
                PartialUnitMode.Ceiling,
                RateRemainderMode.Nearest);
            var start = TehranTime.AddSeconds(T.At(2026, 6, 4, 0, 0), rng.Next(0, 4 * 86400));
            var end = TehranTime.AddSeconds(start, rng.Next(30, 6 * 3600));
            Session session;
            StationDirectory? stations = null;
            if (rng.Next(4) == 0)
            {
                var gapStart = T.At(2026, 6, 5, 22, 0);
                session = new Session(
                    "gap",
                    [
                        new PlaySegment("a", gapStart, gapStart.AddHours(1), "st", groupId, PlayMode.Offline, 1),
                        new PlaySegment("b", T.At(2026, 6, 6, 0, 30), T.At(2026, 6, 6, 1, 30), "st", groupId, PlayMode.Offline, 1),
                    ]);
            }
            else if (rng.Next(3) == 0 && groupId == "g")
            {
                session = ConsolePauseSession(out stations);
                card = new RateCardBuilder().AddBase("ps", PlayMode.Offline, 1, 110_000).Build();
            }
            else
            {
                session = new Session("s", [new PlaySegment("a", start, end, "st", groupId, PlayMode.Offline, 1)]);
            }

            var charge = SessionPricer.Price(session, card, stations: stations, policy: policy);
            var level = rng.Next(3) == 0 ? 0 : rng.Next(1, 40);
            var codePercent = rng.Next(3) == 0 ? 0 : rng.Next(1, 60);
            var fixedToman = rng.Next(3) == 0 ? rng.Next(1, 30_000) : 0L;
            if (codePercent == 0 && fixedToman == 0)
            {
                codePercent = 10;
            }

            DateTimeOffset? validFrom = rng.Next(2) == 0
                ? TehranTime.AddSeconds(charge.Lines[0].Start, rng.Next(-3600, 6 * 3600))
                : null;
            DateTimeOffset? validUntil = rng.Next(2) == 0
                ? TehranTime.AddSeconds(charge.Lines[0].Start, rng.Next(0, 7 * 3600))
                : null;
            DayOfWeek[]? weekdays = rng.Next(4) == 0 ? [(DayOfWeek)rng.Next(7)] : null;
            JalaliDate? birthDate = rng.Next(5) == 0
                ? JalaliDate.FromGregorian(new LocalDate(2026, 6, 6))
                : null;
            var birthdayPercent = birthDate is null ? 0 : rng.Next(1, 25);

            var spec = new SessionDiscount(
                LevelPercent: level,
                BirthdayPercent: birthdayPercent,
                CodePercent: codePercent,
                FixedToman: fixedToman,
                BirthDate: birthDate,
                CodeWeekdays: weekdays,
                ValidFrom: validFrom,
                ValidUntil: validUntil);

            var expectedDiscount = IndependentOracleDiscount(session, charge, policy, spec);
            var balance = balances[rng.Next(balances.Length)];
            TimePackage[] packages = rng.Next(3) == 0
                ? [new TimePackage("p", rng.Next(1, 30), 200_000)]
                : [];
            var order = rng.Next(2) == 0
                ? ConsumptionOrder.PackagesThenMoney
                : ConsumptionOrder.MoneyThenPackages;
            var settled = AccountSettlement.Settle(
                charge,
                new PcAccount("m", balance, packages),
                new AccountPolicy(order),
                billing: policy,
                discount: spec);
            if (packages.Length == 0)
            {
                Assert.Equal(expectedDiscount, settled.DiscountToman);
            }

            var parts = settled.PackageCoveredToman + settled.MoneyConsumedToman + settled.DiscountToman
                + (settled.Grace?.WaivedToman ?? 0) + settled.WriteOffToman + settled.UncoveredToman + settled.OmittedToman;
            Assert.Equal(charge.TotalAmountToman, parts);
        }
    }

    [Fact]
    public void money_first_with_packages_and_codes_matches_minute_oracle()
    {
        var rng = new Random(808);
        var flat = Flat();
        var pilot = PilotSeed.CreateCard();
        var cards = new (RateCard Card, string GroupId)[]
        {
            (flat, "g"),
        };

        for (var i = 0; i < 400; i++)
        {
            var (card, groupId) = cards[rng.Next(cards.Length)];
            var policy = new BillingPolicy(
                rng.Next(3) == 0 ? 1 : (rng.Next(2) == 0 ? 5 : 15),
                PartialUnitMode.Ceiling,
                RateRemainderMode.Nearest);
            var start = TehranTime.AddSeconds(T.At(2026, 6, 4, 0, 0), rng.Next(0, 4 * 86400));
            var end = TehranTime.AddSeconds(start, rng.Next(30, 4 * 3600));
            var session = new Session("s", [new PlaySegment("a", start, end, "st", groupId, PlayMode.Offline, 1)]);
            var charge = SessionPricer.Price(session, card, policy: policy);
            if (charge.Lines.Count != 1)
            {
                continue;
            }

            var line = charge.Lines[0];
            var minuteStarts = BuildSegmentMinuteStarts(session, policy);
            if (minuteStarts.Count != line.BilledMinutes)
            {
                continue;
            }

            var codePercent = rng.Next(3) == 0 ? 0 : rng.Next(1, 60);
            var fixedToman = rng.Next(4) == 0 ? rng.Next(1, 20_000) : 0L;
            DateTimeOffset? validFrom = rng.Next(2) == 0
                ? TehranTime.AddSeconds(line.Start, rng.Next(-3600, (int)(line.End - line.Start).TotalSeconds))
                : null;
            var spec = new SessionDiscount(
                LevelPercent: rng.Next(3) == 0 ? 0 : rng.Next(1, 30),
                CodePercent: codePercent,
                FixedToman: fixedToman,
                ValidFrom: validFrom);
            var packageMinutes = rng.Next(4) == 0 ? 0 : rng.Next(1, 45);
            var balance = rng.Next(3) == 0
                ? rng.Next(0, 50_000)
                : rng.Next(0, 500_000);
            var packages = packageMinutes > 0
                ? new[] { new TimePackage("p", packageMinutes, 200_000) }
                : Array.Empty<TimePackage>();

            var (expectedMoneyMinutes, expectedMoneyNet) = OracleMoneyFirstPrefix(
                line,
                minuteStarts,
                policy,
                spec,
                balance);
            var settled = AccountSettlement.Settle(
                charge,
                new PcAccount("m", balance, packages),
                new AccountPolicy(ConsumptionOrder.MoneyThenPackages),
                billing: policy,
                discount: spec);
            var parts = settled.PackageCoveredToman + settled.MoneyConsumedToman + settled.DiscountToman
                + (settled.Grace?.WaivedToman ?? 0) + settled.WriteOffToman + settled.UncoveredToman + settled.OmittedToman;
            Assert.Equal(charge.TotalAmountToman, parts);
            Assert.True(settled.WriteOffToman >= 0);
            Assert.True(settled.OmittedToman >= 0);
            if (packageMinutes == 0 && balance >= charge.TotalAmountToman - settled.DiscountToman)
            {
                Assert.Equal(expectedMoneyNet, settled.MoneyConsumedToman);
            }

            if (expectedMoneyMinutes > 0)
            {
                Assert.Equal(expectedMoneyMinutes, settled.MoneyDraws[0].Minutes);
            }
        }
    }

    internal static (long Minutes, long NetToman) OracleMoneyFirstPrefix(
        PricedSlice line,
        IReadOnlyList<DateTimeOffset> minuteStarts,
        BillingPolicy policy,
        SessionDiscount spec,
        long balance)
    {
        var clean = line.AmountToman
            == MoneyMath.ChargeForMinutes(line.HourlyRateToman, line.BilledMinutes, policy.Remainder)
                - MoneyMath.ChargeForMinutes(line.HourlyRateToman, 0, policy.Remainder);
        long MinuteGross(long i)
        {
            if (clean)
            {
                return MoneyMath.ChargeForMinutes(line.HourlyRateToman, i + 1, policy.Remainder)
                    - MoneyMath.ChargeForMinutes(line.HourlyRateToman, i, policy.Remainder);
            }

            var before = i == 0 ? 0 : MoneyMath.MulDiv(line.AmountToman, i, line.BilledMinutes);
            var after = i + 1 >= line.BilledMinutes
                ? line.AmountToman
                : MoneyMath.MulDiv(line.AmountToman, i + 1, line.BilledMinutes);
            return after - before;
        }

        long allGross = 0;
        long codeGross = 0;
        long birthGross = 0;
        long prevPercentTotal = 0;
        long carry = 0;
        long fixedLeft = spec.FixedToman;
        long net = 0;
        long minutes = 0;
        var spendable = balance > 0 ? balance : 0;
        for (long i = 0; i < line.BilledMinutes; i++)
        {
            var gross = MinuteGross(i);
            var at = minuteStarts[(int)i];
            var marginal = OracleMarginal(
                ref prevPercentTotal,
                ref carry,
                ref fixedLeft,
                ref allGross,
                ref codeGross,
                ref birthGross,
                gross,
                at,
                line.GroupId,
                spec);
            if (net + marginal > spendable)
            {
                break;
            }

            net += marginal;
            minutes++;
        }

        return (minutes, net);
    }

    internal static long IndependentOracleDiscountForCharge(SessionCharge charge, SessionDiscount spec)
        => IndependentOracleDiscount(
            new Session("s", [new PlaySegment("a", charge.Lines[0].Start, LastLineEnd(charge), "st", charge.Lines[0].GroupId, PlayMode.Offline, 1)]),
            charge,
            BillingPolicy.Default,
            spec);

    private static long Discount(SessionCharge charge, SessionDiscount spec)
        => AccountSettlement.Settle(charge, new PcAccount("m", 100_000_000, []), discount: spec).DiscountToman;

    private static Session GapSession()
        => new Session("gap", [
            new PlaySegment("a", T.At(2026, 6, 5, 22, 0), T.At(2026, 6, 5, 23, 0), "st", "g", PlayMode.Offline, 1),
            new PlaySegment("b", T.At(2026, 6, 6, 0, 30), T.At(2026, 6, 6, 1, 30), "st", "g", PlayMode.Offline, 1),
        ]);

    private static SessionCharge GapLineCharge() => SessionPricer.Price(GapSession(), Flat());

    private static Session ConsolePauseSession(out StationDirectory stations)
    {
        stations = new StationDirectory(
            [new StationGroup("ps", "PS5", StationKind.Console)],
            [new Station("ps-1", "PS5 1", "ps")]);
        return new Session(
            "c",
            [new PlaySegment("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 30), "ps-1", "ps", PlayMode.Offline, 1, StationKind.Console)],
            [new PauseInterval(T.At(2026, 6, 2, 10, 15), T.At(2026, 6, 2, 10, 45))]);
    }

    private static SessionCharge ConsolePauseCharge()
    {
        _ = ConsolePauseSession(out var dir);
        var card = new RateCardBuilder().AddBase("ps", PlayMode.Offline, 1, 110_000).Build();
        return SessionPricer.Price(ConsolePauseSession(out _), card, stations: dir);
    }

    private static long IndependentOracleDiscount(
        Session session,
        SessionCharge charge,
        BillingPolicy policy,
        SessionDiscount spec)
    {
        var minuteStarts = BuildSegmentMinuteStarts(session, policy);
        long allGross = 0;
        long codeGross = 0;
        long birthGross = 0;
        long prevPercentTotal = 0;
        long carry = 0;
        long fixedLeft = spec.FixedToman;
        long netAccum = 0;
        long offset = 0;
        foreach (var line in charge.Lines)
        {
            for (long m = 0; m < line.BilledMinutes; m++)
            {
                var gross = OracleHalfUp(line.HourlyRateToman, m + 1) - OracleHalfUp(line.HourlyRateToman, m);
                var at = minuteStarts[(int)(offset + m)];
                netAccum += OracleMarginal(
                    ref prevPercentTotal,
                    ref carry,
                    ref fixedLeft,
                    ref allGross,
                    ref codeGross,
                    ref birthGross,
                    gross,
                    at,
                    line.GroupId,
                    spec);
            }

            offset += line.BilledMinutes;
        }

        return allGross - netAccum;
    }

    private static List<DateTimeOffset> BuildSegmentMinuteStarts(Session session, BillingPolicy policy)
    {
        var list = new List<DateTimeOffset>();
        foreach (var seg in session.Segments.OrderBy(s => s.Start))
        {
            foreach (var (pieceStart, pieceEnd) in SubtractPauses(seg.Start, seg.End, session.Pauses))
            {
                var start = TehranTime.Ensure(pieceStart);
                var end = TehranTime.Ensure(pieceEnd);
                var activeSeconds = (long)(end - start).TotalSeconds;
                if (activeSeconds <= 0)
                {
                    continue;
                }

                var billed = MoneyMath.BilledMinutes(activeSeconds, policy);
                for (long m = 0; m < billed; m++)
                {
                    list.Add(TehranTime.AddSeconds(start, m * 60L));
                }
            }
        }

        return list;
    }

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> SubtractPauses(
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<PauseInterval>? pauses)
    {
        var pieces = new List<(DateTimeOffset Start, DateTimeOffset End)> { (TehranTime.Ensure(start), TehranTime.Ensure(end)) };
        if (pauses is null || pauses.Count == 0)
        {
            foreach (var piece in pieces)
            {
                yield return piece;
            }

            yield break;
        }

        foreach (var pause in pauses)
        {
            var next = new List<(DateTimeOffset Start, DateTimeOffset End)>();
            foreach (var piece in pieces)
            {
                if (pause.End <= piece.Start || pause.Start >= piece.End)
                {
                    next.Add(piece);
                    continue;
                }

                if (pause.Start > piece.Start)
                {
                    next.Add((piece.Start, pause.Start));
                }

                if (pause.End < piece.End)
                {
                    next.Add((pause.End, piece.End));
                }
            }

            pieces = next;
        }

        foreach (var piece in pieces)
        {
            yield return piece;
        }
    }

    private static long IndependentOracleDiscount(SessionCharge charge, SessionDiscount spec)
        => IndependentOracleDiscount(
            new Session("s", [new PlaySegment("a", charge.Lines[0].Start, LastLineEnd(charge), "st", charge.Lines[0].GroupId, PlayMode.Offline, 1)]),
            charge,
            BillingPolicy.Default,
            spec);

    private static long OracleHalfUp(long rate, long minutes)
        => (rate * minutes * 2 + 60) / 120;

    private static bool CodeEligible(SessionDiscount spec, DateTimeOffset at, string groupId)
    {
        if (spec.CodePercent <= 0 && spec.FixedToman <= 0)
        {
            return false;
        }

        if (spec.ValidFrom is DateTimeOffset from && at < TehranTime.Ensure(from))
        {
            return false;
        }

        if (spec.ValidUntil is DateTimeOffset until && at >= TehranTime.Ensure(until))
        {
            return false;
        }

        if (spec.CodeGroups is { Count: > 0 } groups && !groups.Contains(groupId))
        {
            return false;
        }

        var date = TehranTime.Date(at);
        return spec.CodeWeekdays is not { Count: > 0 } days || days.Contains(date.DayOfWeek);
    }

    private static bool BirthEligible(SessionDiscount spec, DateTimeOffset at)
    {
        if (spec.BirthdayPercent <= 0 || spec.BirthDate is not JalaliDate birth || birth.IsEmpty)
        {
            return false;
        }

        return (spec.Birthday ?? BirthdayWindow.ExactDay).Includes(birth, TehranTime.Date(at));
    }

    private static long OracleMarginal(
        ref long prevPercentTotal,
        ref long carry,
        ref long fixedLeft,
        ref long allGross,
        ref long codeGross,
        ref long birthGross,
        long gross,
        DateTimeOffset at,
        string groupId,
        SessionDiscount spec)
    {
        if (spec.LevelPercent == 0 && spec.BirthdayPercent == 0 && spec.CodePercent == 0 && spec.FixedToman == 0)
        {
            allGross += gross;
            if (CodeEligible(spec, at, groupId))
            {
                codeGross += gross;
            }

            if (BirthEligible(spec, at))
            {
                birthGross += gross;
            }

            return gross;
        }

        var codeOn = CodeEligible(spec, at, groupId);
        var birthOn = BirthEligible(spec, at);
        allGross += gross;
        if (codeOn)
        {
            codeGross += gross;
        }

        if (birthOn)
        {
            birthGross += gross;
        }

        var percentTotal = OraclePercentTotal(allGross, codeGross, birthGross, spec);
        var share = percentTotal - prevPercentTotal;
        prevPercentTotal = percentTotal;
        carry = 0;
        var combinedRate = (birthOn
            ? Math.Min(spec.LevelPercent + spec.BirthdayPercent, spec.Cap)
            : spec.LevelPercent)
            + (codeOn ? spec.CodePercent : 0);
        long percentDisc;
        if (combinedRate >= 100)
        {
            percentDisc = gross;
        }
        else
        {
            percentDisc = share;
            if (percentDisc > gross)
            {
                percentDisc = gross;
            }

            if (percentDisc < 0)
            {
                percentDisc = 0;
            }
        }

        long fixedUse = 0;
        if (codeOn && spec.FixedToman > 0 && fixedLeft > 0)
        {
            var room = gross - percentDisc;
            if (room < 0)
            {
                room = 0;
            }

            fixedUse = fixedLeft < room ? fixedLeft : room;
            fixedLeft -= fixedUse;
        }

        var marginal = gross - percentDisc - fixedUse;
        if (marginal < 0)
        {
            marginal = 0;
        }

        if (marginal > gross)
        {
            marginal = gross;
        }

        return marginal;
    }

    private static long OraclePercentTotal(long all, long codeGross, long birthGross, SessionDiscount spec)
    {
        long levelOff;
        long birthOff;
        if (spec.BirthdayPercent > 0 && birthGross > 0)
        {
            var sum = spec.LevelPercent + spec.BirthdayPercent;
            var rest = all - birthGross;
            if (rest < 0)
            {
                rest = 0;
            }

            if (sum <= spec.Cap)
            {
                levelOff = OraclePercent(rest, spec.LevelPercent) + OraclePercent(birthGross, spec.LevelPercent);
                birthOff = OraclePercent(birthGross, spec.BirthdayPercent);
            }
            else
            {
                var combined = OraclePercent(birthGross, spec.Cap);
                var levelShare = sum == 0 ? 0 : (combined * spec.LevelPercent) / sum;
                levelOff = OraclePercent(rest, spec.LevelPercent) + levelShare;
                birthOff = combined - levelShare;
            }
        }
        else
        {
            levelOff = OraclePercent(all, spec.LevelPercent);
            birthOff = 0;
        }

        var codeOff = OraclePercent(codeGross, spec.CodePercent);
        return levelOff + birthOff + codeOff;
    }

    private static long OraclePercent(long amount, int percent)
        => amount <= 0 || percent <= 0 ? 0 : (amount * percent) / 100;

    private static RateCard Flat() => new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();

    private static DateTimeOffset LastLineEnd(SessionCharge charge)
        => charge.Lines[charge.Lines.Count - 1].End;

    private static SessionCharge Price(RateCard card, DateTimeOffset start, DateTimeOffset end, BillingPolicy? policy)
        => SessionPricer.Price(new Session("s", [
            new PlaySegment("a", start, end, "st", "g", PlayMode.Offline, 1),
        ]), card, policy: policy);

    private static SessionCharge Play(RateCard card, DateTimeOffset start, DateTimeOffset end) => Price(card, start, end, null);

    private static CustomerAccount Wallet()
        => new("m", WalletSettings.Shared with { ApplyDiscountsToPc = true }, new WalletPocket(1_000_000, 0), new WalletPocket(0, 0), []);
}
