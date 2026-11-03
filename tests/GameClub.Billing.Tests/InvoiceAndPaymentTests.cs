using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class InvoiceAndPaymentTests
{
    private static readonly DateTimeOffset Issued = T.At(2026, 6, 2, 18, 0);

    [Fact]
    public void Level_and_birthday_are_added_and_a_code_is_separate()
    {
        var time = Time(200_000);
        var valid = DiscountCode.PercentOff("SAVE15", 15, T.At(2026, 6, 2, 0, 0), T.At(2026, 6, 3, 0, 0));
        var onBirthday = new CustomerPricing("Bronze", 5, new DateOnly(1990, 6, 2), 10);

        var birthday = InvoiceFor(time, onBirthday, valid);
        Assert.Equal(60_000, birthday.DiscountToman);
        Assert.Equal(140_000, birthday.PayableToman);
        Assert.Contains(birthday.Lines, line => line.Kind == InvoiceLineKind.Discount && line.Description.Contains("Birthday", StringComparison.Ordinal) && line.AmountToman == -20_000);
        Assert.Contains(birthday.Lines, line => line.Kind == InvoiceLineKind.Discount && line.Description.Contains("Bronze", StringComparison.Ordinal) && line.AmountToman == -10_000);
        Assert.Null(birthday.RejectedCode);

        var notBirthday = InvoiceFor(time, new CustomerPricing("Bronze", 5, new DateOnly(1990, 1, 1), 10), valid);
        Assert.Equal(40_000, notBirthday.DiscountToman);
        Assert.Equal(160_000, notBirthday.PayableToman);
        Assert.DoesNotContain(notBirthday.Lines, line => line.Description.Contains("Birthday", StringComparison.Ordinal));

        var tooEarly = InvoiceFor(time, onBirthday, valid, T.At(2026, 6, 1, 23, 0));
        Assert.Equal(DiscountRejection.OutsideWindow, tooEarly.RejectedCode);
        Assert.Equal(30_000, tooEarly.DiscountToman);
        Assert.Equal(170_000, tooEarly.PayableToman);

        var tooLate = InvoiceFor(time, onBirthday, valid, T.At(2026, 6, 3, 0, 0));
        Assert.Equal(DiscountRejection.OutsideWindow, tooLate.RejectedCode);
        Assert.Equal(170_000, tooLate.PayableToman);
    }

    [Fact]
    public void Combined_customer_percent_is_capped_at_100_by_default()
    {
        var time = Time(200_000);
        var customer = new CustomerPricing("Titan", 80, new DateOnly(1990, 6, 2), 40);
        var capped = InvoiceFor(time, customer, DiscountCode.PercentOff("NONE", 0));
        Assert.Equal(200_000, capped.DiscountToman);
        Assert.Equal(0, capped.PayableToman);
        Assert.Equal(-133_333, capped.Lines.Single(line => line.Description.Contains("Titan", StringComparison.Ordinal)).AmountToman);
        Assert.Equal(-66_667, capped.Lines.Single(line => line.Description.Contains("Birthday", StringComparison.Ordinal)).AmountToman);

        var tighter = InvoiceFor(time, customer, DiscountCode.PercentOff("NONE", 0), policy: new DiscountPolicy(50, BirthdayWindow.ExactDay));
        Assert.Equal(100_000, tighter.DiscountToman);
        Assert.Equal(100_000, tighter.PayableToman);
    }

    [Fact]
    public void Birthday_window_is_exact_day_a_span_or_the_birth_month()
    {
        var birth = new DateOnly(2000, 6, 2);
        Assert.True(BirthdayWindow.ExactDay.Includes(birth, new DateOnly(2026, 6, 2)));
        Assert.False(BirthdayWindow.ExactDay.Includes(birth, new DateOnly(2026, 6, 3)));

        var weekAfter = BirthdayWindow.DaysOnAndAfter(7);
        Assert.True(weekAfter.Includes(birth, new DateOnly(2026, 6, 8)));
        Assert.False(weekAfter.Includes(birth, new DateOnly(2026, 6, 9)));
        Assert.False(weekAfter.Includes(birth, new DateOnly(2026, 6, 1)));

        var around = BirthdayWindow.DaysAround(7);
        Assert.True(around.Includes(birth, new DateOnly(2026, 5, 30)));
        Assert.True(around.Includes(birth, new DateOnly(2026, 6, 5)));
        Assert.False(around.Includes(birth, new DateOnly(2026, 5, 29)));
        Assert.False(around.Includes(birth, new DateOnly(2026, 6, 6)));

        Assert.True(BirthdayWindow.WholeMonth.Includes(birth, new DateOnly(2026, 6, 15)));
        Assert.False(BirthdayWindow.WholeMonth.Includes(birth, new DateOnly(2026, 7, 1)));

        var yearEnd = new DateOnly(1992, 12, 31);
        var intoJanuary = BirthdayWindow.DaysOnAndAfter(7);
        Assert.True(intoJanuary.Includes(yearEnd, new DateOnly(2026, 1, 6)));
        Assert.False(intoJanuary.Includes(yearEnd, new DateOnly(2026, 1, 7)));

        var leap = new DateOnly(2000, 2, 29);
        Assert.True(BirthdayWindow.ExactDay.Includes(leap, new DateOnly(2024, 2, 29)));
        Assert.True(BirthdayWindow.ExactDay.Includes(leap, new DateOnly(2026, 2, 28)));
        Assert.False(BirthdayWindow.ExactDay.Includes(leap, new DateOnly(2026, 3, 1)));

        var month = InvoiceCalculator.Calculate(new InvoiceRequest(
            [new TimeChargeLine("time", 100_000, CafeFixture.Ps5, new DateOnly(2026, 6, 15), DayOfWeek.Monday)],
            [],
            [],
            new CustomerPricing(null, 0, birth, 10),
            null,
            new DiscountPolicy(100, BirthdayWindow.WholeMonth),
            RoundingPolicy.Off,
            T.At(2026, 6, 15, 12, 0)));
        Assert.Equal(10_000, month.DiscountToman);

        var splitDays = InvoiceCalculator.Calculate(new InvoiceRequest(
            [
                new TimeChargeLine("before", 100_000, CafeFixture.Ps5, new DateOnly(2026, 6, 1), DayOfWeek.Monday),
                new TimeChargeLine("on", 100_000, CafeFixture.Ps5, new DateOnly(2026, 6, 2), DayOfWeek.Tuesday),
            ],
            [],
            [],
            new CustomerPricing("Bronze", 10, birth, 10),
            null,
            DiscountPolicy.Default,
            RoundingPolicy.Off,
            Issued));
        Assert.Equal(30_000, splitDays.DiscountToman);
        Assert.Equal(-20_000, splitDays.Lines.Single(line => line.Description.Contains("Bronze", StringComparison.Ordinal)).AmountToman);
        Assert.Equal(-10_000, splitDays.Lines.Single(line => line.Description.Contains("Birthday", StringComparison.Ordinal)).AmountToman);
    }

    [Fact]
    public void Discount_code_scope_can_combine_time_buffet_and_services()
    {
        var time = new TimeChargeLine("time", 100_000, CafeFixture.Ps5, new DateOnly(2026, 6, 2), DayOfWeek.Tuesday);
        var buffet = new BuffetLine("A_111", "شکلات", 1, 50_000);
        var service = new ServiceLine("extra", "نفر اضافه", 10_000);

        Invoice Scoped(ChargeScope scope) => InvoiceCalculator.Calculate(new InvoiceRequest(
            [time], [buffet], [service], null, DiscountCode.PercentOff("MIX", 10, scope: scope),
            DiscountPolicy.Default, RoundingPolicy.Off, Issued));

        Assert.Equal(10_000, Scoped(ChargeScope.Time).DiscountToman);
        Assert.Equal(150_000, Scoped(ChargeScope.Time).PayableToman);
        Assert.Equal(5_000, Scoped(ChargeScope.Buffet).DiscountToman);
        Assert.Equal(155_000, Scoped(ChargeScope.Buffet).PayableToman);
        Assert.Equal(1_000, Scoped(ChargeScope.Services).DiscountToman);
        Assert.Equal(159_000, Scoped(ChargeScope.Services).PayableToman);
        var both = Scoped(ChargeScope.Time | ChargeScope.Buffet);
        Assert.Equal(15_000, both.DiscountToman);
        Assert.Equal(145_000, both.PayableToman);
        Assert.Contains(both.Lines, line => line.Kind == InvoiceLineKind.Service && line.AmountToman == 10_000);
    }

    [Fact]
    public void Code_group_and_weekday_restrictions_and_a_service_are_not_silently_included()
    {
        var lines = new[]
        {
            new TimeChargeLine("ps5", 100_000, CafeFixture.Ps5, new DateOnly(2026, 6, 2), DayOfWeek.Tuesday),
            new TimeChargeLine("vip", 100_000, CafeFixture.ExampleVip, new DateOnly(2026, 6, 2), DayOfWeek.Tuesday),
        };
        var code = DiscountCode.PercentOff("PS5", 10, allowedGroupIds: new HashSet<string> { CafeFixture.Ps5 });
        var invoice = InvoiceCalculator.Calculate(new InvoiceRequest(
            lines, [], [], null, code, DiscountPolicy.Default, RoundingPolicy.Off, Issued));
        Assert.Equal(10_000, invoice.DiscountToman);
        Assert.Equal(190_000, invoice.PayableToman);

        var fridayOnly = DiscountCode.PercentOff("FRI", 10, allowedWeekdays: new HashSet<DayOfWeek> { DayOfWeek.Friday });
        var rejected = InvoiceCalculator.Calculate(new InvoiceRequest(
            lines, [], [], null, fridayOnly, DiscountPolicy.Default, RoundingPolicy.Off, Issued));
        Assert.Equal(DiscountRejection.NotEligible, rejected.RejectedCode);
        Assert.Equal(0, rejected.DiscountToman);

        var withFine = InvoiceCalculator.Calculate(new InvoiceRequest(
            [new TimeChargeLine("ps5", 200_000, CafeFixture.Ps5, new DateOnly(2026, 6, 2), DayOfWeek.Tuesday)],
            [],
            [new ServiceLine("ABC_1", "خسارت انداختن دسته", GameTimerReference.ControllerDamageFineToman)],
            new CustomerPricing(null, 0, new DateOnly(1990, 6, 2), 10),
            null,
            DiscountPolicy.Default,
            RoundingPolicy.Off,
            Issued));
        Assert.Equal(20_000, withFine.DiscountToman);
        Assert.Equal(1_080_000, withFine.PayableToman);
    }

    [Fact]
    public void Discounts_cannot_push_the_payable_total_below_zero()
    {
        var invoice = InvoiceCalculator.Calculate(new InvoiceRequest(
            [new TimeChargeLine("time", 80_000, CafeFixture.Ps5, new DateOnly(2026, 6, 2), DayOfWeek.Tuesday)],
            [new BuffetLine("A_111", "شکلات", 1, 20_000)],
            [],
            new CustomerPricing("Titan", 100, null, 0),
            DiscountCode.PercentOff("HALF", 50),
            DiscountPolicy.Default,
            RoundingPolicy.Create(5_000),
            Issued));

        Assert.Equal(80_000, invoice.DiscountToman);
        Assert.Equal(20_000, invoice.PayableToman);
        Assert.True(invoice.PayableToman >= 0);
        Assert.Equal(invoice.Lines.Sum(line => line.AmountToman), invoice.PayableToman);
    }

    [Theory]
    [MemberData(nameof(RoundingCases))]
    public void Rounding_matches_the_branch_policy(long amount, long step, RoundingDirection direction, bool enabled, long expected, long delta)
    {
        var policy = enabled
            ? RoundingPolicy.Create(step, direction)
            : new RoundingPolicy(false, step, direction, RoundingScope.WholeInvoice);
        var invoice = InvoiceCalculator.Calculate(new InvoiceRequest(
            [new TimeChargeLine("time", amount, null, null, null)],
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            policy,
            Issued));

        Assert.Equal(expected, invoice.PayableToman);
        Assert.Equal(delta, invoice.RoundingToman);
        if (delta == 0)
        {
            Assert.DoesNotContain(invoice.Lines, line => line.Kind == InvoiceLineKind.Rounding);
        }
        else
        {
            var rounding = Assert.Single(invoice.Lines, line => line.Kind == InvoiceLineKind.Rounding);
            Assert.Equal(delta, rounding.AmountToman);
        }
    }

    public static IEnumerable<object[]> RoundingCases()
    {
        yield return [138_700L, 5_000L, RoundingDirection.Up, true, 140_000L, 1_300L];
        yield return [138_700L, 5_000L, RoundingDirection.Down, true, 135_000L, -3_700L];
        yield return [138_700L, 5_000L, RoundingDirection.Nearest, true, 140_000L, 1_300L];
        yield return [137_500L, 5_000L, RoundingDirection.Nearest, true, 140_000L, 2_500L];
        yield return [138_700L, 5_000L, RoundingDirection.Up, false, 138_700L, 0L];
        yield return [138_700L, 2_500L, RoundingDirection.Up, true, 140_000L, 1_300L];
        yield return [138_700L, 2_500L, RoundingDirection.Nearest, true, 137_500L, -1_200L];
        yield return [138_700L, 2_500L, RoundingDirection.Down, true, 137_500L, -1_200L];
        yield return [138_700L, 1_000L, RoundingDirection.Up, true, 139_000L, 300L];
        yield return [138_700L, 10_000L, RoundingDirection.Up, true, 140_000L, 1_300L];
        yield return [138_700L, 10_000L, RoundingDirection.Down, true, 130_000L, -8_700L];
    }

    [Fact]
    public void Rounding_runs_after_discounts_and_scope_changes_what_is_rounded()
    {
        var afterDiscount = InvoiceCalculator.Calculate(new InvoiceRequest(
            [new TimeChargeLine("time", 200_000, CafeFixture.Ps5, new DateOnly(2026, 6, 2), DayOfWeek.Tuesday)],
            [],
            [],
            null,
            DiscountCode.FixedOff("CUT", 61_300),
            DiscountPolicy.Default,
            RoundingPolicy.Create(5_000, RoundingDirection.Up),
            Issued));
        Assert.Equal(138_700, afterDiscount.NetBeforeRoundingToman);
        Assert.Equal(140_000, afterDiscount.PayableToman);
        Assert.Equal(1_300, afterDiscount.RoundingToman);

        var time = new TimeChargeLine("time", 98_700, CafeFixture.Ps5, new DateOnly(2026, 6, 2), DayOfWeek.Tuesday);
        var service = new ServiceLine("extra", "نفر اضافه", 3_000);
        var whole = InvoiceCalculator.Calculate(new InvoiceRequest(
            [time], [], [service], null, null, DiscountPolicy.Default,
            RoundingPolicy.Create(5_000, RoundingDirection.Up, RoundingScope.WholeInvoice), Issued));
        var timeOnly = InvoiceCalculator.Calculate(new InvoiceRequest(
            [time], [], [service], null, null, DiscountPolicy.Default,
            RoundingPolicy.Create(5_000, RoundingDirection.Up, RoundingScope.TimeChargesOnly), Issued));

        Assert.Equal(105_000, whole.PayableToman);
        Assert.Equal(3_300, whole.RoundingToman);
        Assert.Equal(103_000, timeOnly.PayableToman);
        Assert.Equal(1_300, timeOnly.RoundingToman);
        Assert.Equal(whole.Lines.Sum(line => line.AmountToman), whole.PayableToman);
        Assert.Equal(timeOnly.NetBeforeRoundingToman + timeOnly.RoundingToman, timeOnly.PayableToman);
    }

    [Fact]
    public void A_zero_net_is_not_rounded_up_to_the_step()
    {
        var invoice = InvoiceCalculator.Calculate(new InvoiceRequest(
            [new TimeChargeLine("time", 10_000, null, null, null)],
            [],
            [],
            new CustomerPricing(null, 100, null, 0),
            null,
            DiscountPolicy.Default,
            RoundingPolicy.Create(5_000),
            Issued));
        Assert.Equal(0, invoice.PayableToman);
        Assert.Equal(0, invoice.RoundingToman);
    }

    [Fact]
    public void Split_payment_checks_the_sum_and_splits_wallet_credit_in_proportion()
    {
        var methods = PaymentSplitter.Split(140_000, [
            new Tender(PaymentMethod.Cash, 40_000),
            new Tender(PaymentMethod.CardReader, 50_000),
            new Tender(PaymentMethod.CardToCard, 50_000),
        ]);
        Assert.True(methods.IsValid);
        Assert.Equal(0, methods.DifferenceToman);
        Assert.Equal(40_000, methods.MethodTotals.Single(total => total.Method == PaymentMethod.Cash).AmountToman);
        Assert.Equal(50_000, methods.MethodTotals.Single(total => total.Method == PaymentMethod.CardReader).AmountToman);
        Assert.Equal(50_000, methods.MethodTotals.Single(total => total.Method == PaymentMethod.CardToCard).AmountToman);

        var wallets = new[]
        {
            new MemberWallet("A", 50_000, 10_000),
            new MemberWallet("B", 80_000, 20_000),
        };
        var proportional = PaymentSplitter.Split(70_000, [
            new Tender(PaymentMethod.Wallet, 30_000, "A"),
            new Tender(PaymentMethod.Wallet, 40_000, "B"),
        ], wallets);
        Assert.True(proportional.IsValid);
        Assert.Equal(5_000, proportional.Tenders[0].GiftDrawnToman);
        Assert.Equal(25_000, proportional.Tenders[0].RealDrawnToman);
        Assert.Equal(8_000, proportional.Tenders[1].GiftDrawnToman);
        Assert.Equal(32_000, proportional.Tenders[1].RealDrawnToman);
        Assert.Equal(25_000, proportional.RemainingWallets.Single(wallet => wallet.MemberId == "A").RealCreditToman);
        Assert.Equal(5_000, proportional.RemainingWallets.Single(wallet => wallet.MemberId == "A").GiftCreditToman);
        Assert.Equal(48_000, proportional.RemainingWallets.Single(wallet => wallet.MemberId == "B").RealCreditToman);
        Assert.Equal(12_000, proportional.RemainingWallets.Single(wallet => wallet.MemberId == "B").GiftCreditToman);

        var shortWallet = PaymentSplitter.Split(60_001, [
            new Tender(PaymentMethod.Wallet, 60_001, "A"),
        ], wallets);
        Assert.False(shortWallet.IsValid);
        Assert.Contains(shortWallet.Errors, error => error.Code == PaymentErrors.InsufficientWallet);
        var untouched = shortWallet.RemainingWallets.Single(wallet => wallet.MemberId == "A");
        Assert.Equal(60_000, untouched.RealCreditToman + untouched.GiftCreditToman);

        var mismatch = PaymentSplitter.Split(140_000, [new Tender(PaymentMethod.Cash, 100_000)]);
        Assert.False(mismatch.IsValid);
        Assert.Equal(-40_000, mismatch.DifferenceToman);
        Assert.Contains(mismatch.Errors, error => error.Code == PaymentErrors.SumMismatch);

        var running = PaymentSplitter.Split(100_000, [
            new Tender(PaymentMethod.Wallet, 60_000, "B"),
            new Tender(PaymentMethod.Wallet, 40_000, "B"),
        ], [new MemberWallet("B", 100_000, 0)]);
        Assert.True(running.IsValid);
        Assert.Equal(0, running.RemainingWallets.Single().RealCreditToman);
    }

    private static TimeChargeLine Time(long amount)
        => new("time", amount, CafeFixture.Ps5, new DateOnly(2026, 6, 2), DayOfWeek.Tuesday);

    private static Invoice InvoiceFor(
        TimeChargeLine time,
        CustomerPricing customer,
        DiscountCode code,
        DateTimeOffset? issuedAt = null,
        DiscountPolicy? policy = null)
    {
        return InvoiceCalculator.Calculate(new InvoiceRequest(
            [time],
            [],
            [],
            customer,
            code,
            policy ?? DiscountPolicy.Default,
            RoundingPolicy.Off,
            issuedAt ?? Issued));
    }
}
