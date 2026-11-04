using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class ReReview2Tests
{
    private static readonly CustomerPricing Titan = new("Titan", 28, null, 0);

    [Fact]
    public void R2_1_discounted_settlement_debits_the_net_of_the_whole_session()
    {
        var hour = Hour(T.At(2026, 6, 2, 10, 0));
        var full = CustomerPlay.Settle(hour, Wallet(79_200), pricing: Titan);
        Assert.Equal(79_200, full.Settlement.MoneyConsumedToman);
        Assert.Equal(0, full.Settlement.UncoveredToman);
        Assert.Equal(0, full.Settlement.WriteOffToman);
        Assert.Equal(0, full.Account.PcMoneyToman);
        Assert.Null(full.Settlement.Grace);

        var partial = CustomerPlay.Settle(hour, Wallet(50_000), pricing: Titan);
        Assert.Equal(50_000, partial.Settlement.MoneyConsumedToman);
        Assert.Equal(0, partial.Settlement.UncoveredToman);
        Assert.Equal(0, partial.Account.PcMoneyToman);
        Assert.True(partial.Settlement.WriteOffToman > 0);
        Assert.Equal("pc_overshoot", partial.Settlement.Alert!.Code);

        var minute = MoneyMath.ChargeForMinutes(110_000, 1, RateRemainderMode.Nearest);
        var one = CustomerPlay.Settle(OneMinute(), Wallet(1_500), pricing: Titan);
        var net = new PcTimeDiscount(28, 0, 0).Net(minute);
        Assert.Null(one.Settlement.Grace);
        Assert.Equal(net, one.Settlement.MoneyConsumedToman);
        Assert.Equal(1_500 - net, one.Account.PcMoneyToman);
        Assert.Equal(0, one.Settlement.UncoveredToman);
    }

    [Fact]
    public void R2_2_block_grace_is_one_minute_and_the_invoice_shows_the_session_gross()
    {
        var block = MoneyMath.ChargeForMinutes(110_000, 5, RateRemainderMode.Nearest);
        var policy = new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var plan = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 10, 0),
            (2 * block) + 500,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card(),
            policy: policy);
        Assert.Equal(1, plan.Grace!.Minutes);
        Assert.Equal(11, plan.Lines.Sum(line => line.BilledMinutes));
        Assert.Equal((2 * block) + 500 - MoneyMath.ChargeForMinutes(110_000, 10, RateRemainderMode.Nearest), plan.UnusedToman);

        var minute = MoneyMath.ChargeForMinutes(110_000, 1, RateRemainderMode.Nearest);
        foreach (var granularity in new[] { 5, 15 })
        {
            var coarse = new BillingPolicy(granularity, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
            var played = SessionPricer.Price(new Session("s", [
                CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 10, 0, 1), "ps5-1", CafeFixture.Ps5, 1),
            ]), CafeFixture.Card(), policy: coarse);
            var play = CustomerPlay.Settle(played, Wallet(500), billing: coarse);
            Assert.Equal(minute, play.ChargeInvoice!.TimeGrossToman);
            Assert.Equal(minute, play.Settlement.Grace!.WaivedToman);
            Assert.Equal(0, play.Settlement.MoneyConsumedToman);
            Assert.Equal(500, play.Account.PcMoneyToman);
            Assert.Equal(0, play.Settlement.WriteOffToman);
            Assert.Null(play.Settlement.Alert);
            Assert.DoesNotContain(play.ChargeInvoice.Lines, line => line.Kind == InvoiceLineKind.WriteOff);
        }
    }

    [Fact]
    public void R2_3_grace_uses_the_marginal_minute_and_matches_settlement()
    {
        var plan = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 10, 0),
            3_666,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        Assert.Equal(T.At(2026, 6, 2, 10, 2), plan.End);
        Assert.Equal(1, plan.Grace!.WaivedToman);
        Assert.Equal(3_666, plan.ConsumedToman);
        Assert.Equal(0, plan.UnusedToman);
        var marginal = MoneyMath.ChargeForMinutes(110_000, 2, RateRemainderMode.Nearest)
            - MoneyMath.ChargeForMinutes(110_000, 1, RateRemainderMode.Nearest);
        Assert.Equal(marginal - (3_666 - MoneyMath.ChargeForMinutes(110_000, 1, RateRemainderMode.Nearest)), plan.Grace.WaivedToman);

        var mismatches = 0;
        var sample = "";
        var rng = new Random(42);
        const int samples = 800;
        for (var i = 0; i < samples; i++)
        {
            var budget = rng.Next(1, 400_000);
            var start = TehranTime.AddSeconds(T.At(2026, 6, 1, 0, 0), rng.Next(0, 5 * 86_400));
            var priced = PrepaidPlanner.ForAmount(start, budget, "a", "st", CafeFixture.Ps5, PlayMode.Offline, 1, CafeFixture.Card());
            var played = SessionPricer.Price(new Session("x", [
                new PlaySegment("a", priced.Start, priced.End, "st", CafeFixture.Ps5, PlayMode.Offline, 1),
            ]), CafeFixture.Card());
            var settled = AccountSettlement.Settle(played, new PcAccount("m", budget, []));
            if (settled.MoneyConsumedToman != priced.ConsumedToman
                || settled.UncoveredToman != 0
                || (settled.Grace?.WaivedToman ?? 0) != (priced.Grace?.WaivedToman ?? 0))
            {
                mismatches++;
                if (sample.Length == 0)
                {
                    sample = $"b={budget} start={start:o} plan={priced.ConsumedToman}/{priced.Grace?.WaivedToman} end={priced.End:o} settle={settled.MoneyConsumedToman}/{settled.Grace?.WaivedToman} uncovered={settled.UncoveredToman} writeOff={settled.WriteOffToman} played={played.TotalAmountToman}";
                }
            }
        }

        Assert.True(mismatches == 0, $"{mismatches}/{samples} {sample}");
    }

    [Fact]
    public void R2_4_collect_rounds_only_the_counter_remainder()
    {
        var issued = T.At(2026, 6, 2, 12, 0);
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 149_850, null, null, null)],
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            RoundingPolicy.Create(5_000),
            issued);
        var calculated = InvoiceCalculator.Calculate(request);
        Assert.Equal(150_000, calculated.PayableToman);
        var (cashInvoice, cashAllocation) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Cash, 150_000)]);
        Assert.True(cashAllocation.IsValid, cashAllocation.Errors.FirstOrDefault()?.Message);
        Assert.Equal(calculated.PayableToman, cashInvoice.PayableToman);
        Assert.Equal(calculated.PayableToman, cashAllocation.PayableToman);

        var (mixed, allocation) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 50_001, "m"), new Tender(PaymentMethod.Cash, 100_000)],
            [new MemberWallet("m", 50_001, 0)]);
        Assert.True(allocation.IsValid, allocation.Errors.FirstOrDefault()?.Message);
        Assert.Equal(150_001, allocation.PayableToman);
        Assert.Equal(allocation.PayableToman, mixed.PayableToman);
    }

    [Fact]
    public void R2_5_pc_balance_draws_the_pocket_and_errors_without_a_pc_line()
    {
        var pocket = new WalletPocket(1_000, 500);
        var paid = PaymentAllocator.Allocate(
            [new PayableLine("pc-normal", true, true, 1_500)],
            [new Tender(PaymentMethod.PcBalance, 1_500)],
            RoundingPolicy.Off,
            pcBalance: pocket);
        Assert.True(paid.IsValid, paid.Errors.FirstOrDefault()?.Message);
        Assert.Equal(1_500, paid.Tenders[0].AmountToman);
        Assert.True(paid.Tenders[0].GiftDrawnToman > 0);
        Assert.Equal(1_500, paid.Tenders[0].GiftDrawnToman + paid.Tenders[0].RealDrawnToman);

        var missing = PaymentAllocator.Allocate(
            [new PayableLine(CafeFixture.Ps5, false, true, 10_000)],
            [new Tender(PaymentMethod.PcBalance, 1_000), new Tender(PaymentMethod.Cash, 10_000)],
            RoundingPolicy.Off);
        Assert.False(missing.IsValid);
        Assert.Contains(missing.Errors, error => error.Code == PaymentErrors.PcBalanceNoPcLine);
    }

    [Fact]
    public void R2_6_wallet_debt_does_not_cover_pc_lines()
    {
        var result = PaymentAllocator.Allocate(
            [
                new PayableLine("pc-normal", true, true, 30_000),
                new PayableLine(CafeFixture.Ps5, false, true, 30_000),
            ],
            [new Tender(PaymentMethod.Wallet, 60_000, "m"), new Tender(PaymentMethod.Cash, 30_000)],
            RoundingPolicy.Off,
            [new MemberWallet("m", 0, 0)],
            DebtPolicy.Unlimited);
        Assert.True(result.IsValid, result.Errors.FirstOrDefault()?.Message);
        Assert.Equal(30_000, result.Tenders[0].AmountToman);
        Assert.Equal(-30_000, result.RemainingWallets.Single().BalanceToman);
        Assert.Equal(0, result.Shares.Where(share => share.LineIndex == 0).Sum(share => share.AmountToman));
        Assert.Equal(30_000, result.Shares.Where(share => share.LineIndex == 1).Sum(share => share.AmountToman));
    }

    [Fact]
    public void R2_7_pc_settlement_does_not_round_the_balance()
    {
        var hour = Hour(T.At(2026, 6, 2, 10, 0));
        var play = CustomerPlay.Settle(hour, Wallet(110_000));
        Assert.Equal(110_000, play.Settlement.MoneyConsumedToman);
        Assert.Equal(0, play.Account.PcMoneyToman);
        Assert.Equal(110_000, play.ChargeInvoice!.PayableToman);
    }

    [Fact]
    public void R2_8_money_time_lines_are_gross_so_grace_is_waived_once()
    {
        var minute = MoneyMath.ChargeForMinutes(110_000, 1, RateRemainderMode.Nearest);
        var settled = AccountSettlement.Settle(OneMinute(), new PcAccount("m", 500, []));
        var lines = settled.MoneyTimeLines();
        Assert.Equal(minute, lines.Sum(line => line.AmountToman));
        var invoice = InvoiceCalculator.Calculate(new InvoiceRequest(
            lines.ToArray(),
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            RoundingPolicy.Off,
            T.At(2026, 6, 2, 10, 1),
            settled.Grace));
        Assert.Equal(500, invoice.PayableToman);
    }

    [Fact]
    public void R2_9_pro_rata_of_large_lines_does_not_throw_overflow()
    {
        var lines = new[]
        {
            new PayableLine(null, false, true, 4_000_000_000),
            new PayableLine(null, false, true, 4_000_000_000),
        };
        var result = PaymentAllocator.Allocate(
            lines,
            [new Tender(PaymentMethod.Wallet, 4_000_000_000, "m"), new Tender(PaymentMethod.Cash, 4_000_000_000)],
            RoundingPolicy.Off,
            [new MemberWallet("m", 4_000_000_000, 0)]);
        Assert.True(result.IsValid, result.Errors.FirstOrDefault()?.Message);
        Assert.Equal(4_000_000_000, result.BalanceAppliedToman);
        Assert.Equal(2_000_000_000, result.Shares[0].AmountToman);
        Assert.Equal(2_000_000_000, result.Shares[1].AmountToman);
    }

    [Fact]
    public void R2_10_a_long_session_and_a_large_budget_price_by_rate_boundary()
    {
        var started = DateTime.UtcNow;
        var plan = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 10, 0),
            10_000_000,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        var year = SessionPricer.Price(new Session("year", [
            CafeFixture.Play("a", T.At(2026, 1, 1, 0, 0), T.At(2027, 1, 1, 0, 0), "ps5-1", CafeFixture.Ps5, 1),
        ]), CafeFixture.Card());
        Assert.True(plan.End > plan.Start);
        Assert.True(year.TotalBilledMinutes > 500_000);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void R2_11_a_small_rate_is_not_unlimited_and_a_free_band_ends()
    {
        var cheap = new RateCardBuilder()
            .AddBase(CafeFixture.Ps5, PlayMode.Offline, 1, 30)
            .Build();
        var small = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 10, 0),
            100_000,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            cheap);
        Assert.False(small.IsUnlimited);
        Assert.True(small.End > small.Start.AddMinutes(2));

        var freeBand = new RateCardBuilder()
            .AddBase(CafeFixture.Ps5, PlayMode.Offline, 1, 110_000)
            .AddBand(new TimeBand("free", CafeFixture.Ps5, "free", new TimeOfDay(10, 0), new TimeOfDay(10, 1)))
            .AddBandRate("free", CafeFixture.Ps5, PlayMode.Offline, 1, 0)
            .Build();
        var plan = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 10, 0),
            100_000,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            freeBand);
        Assert.False(plan.IsUnlimited);
        Assert.Equal(T.At(2026, 6, 2, 10, 1), plan.FreeUntil);
        Assert.True(plan.PaidMinutesAfterFree > 1);
        Assert.True(plan.End > T.At(2026, 6, 2, 10, 2));
    }

    [Fact]
    public void R2_12_a_zero_minute_segment_does_not_block_the_same_rate_merge()
    {
        var start = T.At(2026, 6, 2, 18, 0);
        var charge = SessionPricer.Price(new Session("s", [
            CafeFixture.Play("a", start, TehranTime.AddSeconds(start, 30), "ps5-1", CafeFixture.Ps5, 2),
            CafeFixture.Play("b", TehranTime.AddSeconds(start, 30), TehranTime.AddSeconds(start, 50), "ps5-1", CafeFixture.Ps5, 4),
            CafeFixture.Play("c", TehranTime.AddSeconds(start, 50), TehranTime.AddSeconds(start, 120), "ps5-1", CafeFixture.Ps5, 2),
        ]), CafeFixture.Card());
        var line = Assert.Single(charge.Lines);
        Assert.Equal(2, line.BilledMinutes);
        Assert.Equal(MoneyMath.ChargeForMinutes(GameTimerReference.Ps5TwoPlayerHourly, 2, RateRemainderMode.Nearest), line.AmountToman);
        Assert.Equal(4_167, line.AmountToman);
    }

    [Fact]
    public void R2_13_the_default_rounding_scope_is_the_whole_invoice()
    {
        Assert.Equal(RoundingScope.WholeInvoice, default(RoundingScope));
        Assert.Equal(0, (int)RoundingScope.WholeInvoice);
        Assert.Equal(RoundingScope.WholeInvoice, RoundingPolicy.Create(1_000).Scope);
    }

    [Fact]
    public void R2_14_a_same_rate_line_keeps_one_price_and_a_per_date_allocation()
    {
        var flat = new RateCardBuilder()
            .AddBase(CafeFixture.Ps5, PlayMode.Offline, 1, 110_000)
            .Build();
        var charge = SessionPricer.Price(new Session("night", [
            CafeFixture.Play("a", T.At(2026, 6, 2, 23, 0), T.At(2026, 6, 3, 1, 0), "ps5-1", CafeFixture.Ps5, 1),
        ]), flat);
        var line = Assert.Single(charge.Lines);
        Assert.Equal(2, line.ByDate!.Count);
        Assert.Equal(60, line.ByDate[0].Minutes);
        Assert.Equal(new LocalDate(2026, 6, 2), line.ByDate[0].Date);
        Assert.Equal(60, line.ByDate[1].Minutes);
        Assert.Equal(new LocalDate(2026, 6, 3), line.ByDate[1].Date);
        Assert.Equal(line.BilledMinutes, line.ByDate.Sum(part => part.Minutes));

        var invoiceLines = TimeChargeLine.FromSession(charge);
        Assert.Equal(2, invoiceLines.Count);
        Assert.Equal(charge.TotalAmountToman, invoiceLines.Sum(item => item.AmountToman));
        Assert.Equal(new LocalDate(2026, 6, 2), invoiceLines[0].ServiceDate);
        Assert.Equal(new LocalDate(2026, 6, 3), invoiceLines[1].ServiceDate);
    }

    [Fact]
    public void R2_15_an_empty_date_reports_invalid_date()
    {
        var error = Assert.Throws<BillingException>(() => _ = default(LocalDate).DayOfWeek);
        Assert.Equal(BillingErrors.InvalidDate, error.Code);
    }

    [Fact]
    public void Owner_4_ceil_seconds_and_a_sub_minute_session_bills_one_minute()
    {
        var start = T.At(2026, 6, 2, 10, 0);
        var charge = SessionPricer.Price(new Session("s", [
            CafeFixture.Play("a", start, TehranTime.AddSeconds(start, 30), "ps5-1", CafeFixture.Ps5, 1),
        ]), CafeFixture.Card());
        Assert.Equal(1, charge.TotalBilledMinutes);
        Assert.Equal(MoneyMath.ChargeForMinutes(110_000, 1, RateRemainderMode.Nearest), charge.TotalAmountToman);
    }

    [Fact]
    public void Owner_5_a_balance_between_one_minute_and_one_block_buys_one_minute()
    {
        var policy = new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var plan = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 10, 0),
            5_000,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card(),
            policy: policy);
        var minute = MoneyMath.ChargeForMinutes(110_000, 1, RateRemainderMode.Nearest);
        Assert.Equal(1, plan.Lines.Sum(line => line.BilledMinutes));
        Assert.Equal(minute, plan.Grace!.WaivedToman);
        Assert.Equal(0, plan.ConsumedToman);
        Assert.Equal(5_000, plan.UnusedToman);
    }

    private static CustomerAccount Wallet(long balance)
        => new("m", WalletSettings.Shared with { ApplyDiscountsToPc = true }, new WalletPocket(balance, 0), new WalletPocket(0, 0), []);

    private static SessionCharge Hour(DateTimeOffset start)
        => CafeFixture.Price(CafeFixture.Session(
            "hour",
            CafeFixture.Play("a", start, start.AddHours(1), "ps5-1", CafeFixture.Ps5, 1)));

    private static SessionCharge OneMinute()
    {
        var start = T.At(2026, 6, 2, 10, 0);
        return CafeFixture.Price(CafeFixture.Session(
            "minute",
            CafeFixture.Play("a", start, start.AddMinutes(1), "ps5-1", CafeFixture.Ps5, 1)));
    }
}
