using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class Round4Tests
{
    private static readonly CustomerPricing Titan = new("Titan", 28, null, 0);

    [Fact]
    public void A_fixed_code_is_taken_once_and_a_percent_once_on_the_session_total()
    {
        var card = PilotSeed.CreateCard();
        var vip = PilotSeed.PcVipGroupId;
        var fixedCode = new DiscountCode("F5", 0, 5_000, null, null, null, null);

        var band = Play(card, vip, T.At(2026, 6, 2, 8, 20), T.At(2026, 6, 2, 8, 40));
        var bandPay = CustomerPlay.Settle(band, Account(1_000_000), code: fixedCode);
        Assert.Equal(35_000, bandPay.Settlement.MoneyConsumedToman);
        Assert.Equal(5_000, bandPay.Settlement.DiscountToman);

        var edge = Play(card, vip, T.At(2026, 6, 2, 7, 50), T.At(2026, 6, 2, 9, 10));
        var edgePay = CustomerPlay.Settle(edge, Account(1_000_000), pricing: Titan, code: fixedCode);
        Assert.Equal(110_200, edgePay.Settlement.MoneyConsumedToman);
        Assert.Equal(edgePay.Settlement.MoneyConsumedToman, edgePay.ChargeInvoice!.PayableToman);

        var midnight = Play(card, vip, T.At(2026, 6, 2, 23, 30), T.At(2026, 6, 3, 0, 40));
        var midnightPay = CustomerPlay.Settle(midnight, Account(1_000_000), pricing: Titan, code: fixedCode, issuedAt: T.At(2026, 6, 3, 0, 40));
        Assert.Equal(97_001, midnightPay.Settlement.MoneyConsumedToman);
        Assert.Equal(midnightPay.Settlement.MoneyConsumedToman, midnightPay.ChargeInvoice!.PayableToman);
    }

    [Fact]
    public void A_weekday_code_and_a_birthday_follow_each_minute_date()
    {
        var card = PilotSeed.CreateCard();
        var vip = PilotSeed.PcVipGroupId;
        var saturday = DiscountCode.PercentOff("SAT", 50, allowedWeekdays: new[] { DayOfWeek.Saturday });
        var fridayNight = T.At(2026, 6, 5, 23, 0);
        var fromFriday = CustomerPlay.Settle(
            Play(card, vip, fridayNight, fridayNight.AddHours(2)),
            Account(1_000_000),
            code: saturday,
            issuedAt: fridayNight.AddHours(2));
        var saturdayOnly = MoneyMath.PercentOf(130_000, 50);
        Assert.Equal(saturdayOnly, fromFriday.Settlement.DiscountToman);

        var saturdayNight = T.At(2026, 6, 6, 23, 0);
        var fromSaturday = CustomerPlay.Settle(
            Play(card, vip, saturdayNight, saturdayNight.AddHours(2)),
            Account(1_000_000),
            code: saturday,
            issuedAt: saturdayNight.AddHours(2));
        Assert.Equal(MoneyMath.PercentOf(110_000, 50), fromSaturday.Settlement.DiscountToman);

        var birth = JalaliDate.FromGregorian(new LocalDate(2026, 6, 6));
        var pricing = new CustomerPricing("Guest", 0, birth, 50);
        var birthday = CustomerPlay.Settle(
            Play(card, vip, fridayNight, fridayNight.AddHours(2)),
            Account(1_000_000),
            pricing: pricing,
            issuedAt: fridayNight.AddHours(2));
        Assert.Equal(MoneyMath.PercentOf(130_000, 50), birthday.Settlement.DiscountToman);
    }

    [Fact]
    public void Block_grace_is_free_and_a_normal_stop_is_not_overshoot()
    {
        var policy = new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var start = T.At(2026, 6, 2, 10, 0);
        var played = SessionPricer.Price(new Session("s", [
            new PlaySegment("a", start, TehranTime.AddSeconds(start, 1), "pc-1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, StationKind.Pc),
        ]), PilotSeed.CreateCard(), policy: policy);
        var account = new CustomerAccount("m", WalletSettings.Shared, new WalletPocket(5_000, 0), new WalletPocket(0, 0), []);
        var play = CustomerPlay.Settle(played, account, billing: policy);
        var minute = MoneyMath.ChargeForMinutes(PilotSeed.PcNormalHourly, 1, RateRemainderMode.Nearest);
        Assert.Equal(0, play.Settlement.MoneyConsumedToman);
        Assert.Equal(minute, play.Settlement.Grace!.WaivedToman);
        Assert.Equal(5_000, play.Account.PcMoneyToman);
        Assert.Equal(0, play.Settlement.WriteOffToman);
        Assert.Null(play.Settlement.Alert);
        Assert.Null(play.Settlement.ReportEntry);
        Assert.Equal(minute, play.ChargeInvoice!.TimeGrossToman);
        Assert.DoesNotContain(play.ChargeInvoice.Lines, line => line.Kind == InvoiceLineKind.WriteOff);
    }

    [Fact]
    public void Play_past_the_grace_minute_is_the_only_pc_overshoot()
    {
        var policy = new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var start = T.At(2026, 6, 2, 10, 0);
        var played = SessionPricer.Price(new Session("s", [
            new PlaySegment("a", start, start.AddMinutes(15), "pc-1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, StationKind.Pc),
        ]), PilotSeed.CreateCard(), policy: policy);
        var account = new CustomerAccount("m", WalletSettings.Shared, new WalletPocket(5_000, 0), new WalletPocket(0, 0), []);
        var play = CustomerPlay.Settle(played, account, billing: policy);
        Assert.True(play.Settlement.WriteOffToman > 0);
        Assert.Equal("pc_overshoot", play.Settlement.Alert!.Code);
        Assert.Equal("write_off", play.Settlement.ReportEntry!.Kind);
    }

    [Fact]
    public void A_pause_is_judged_per_segment_and_an_unknown_kind_is_an_error()
    {
        var stations = new StationDirectory(
            [
                new StationGroup("pcg", "PC", StationKind.Pc),
                new StationGroup("ps", "PS5", StationKind.Console),
            ],
            [
                new Station("pc-1", "PC", "pcg"),
                new Station("ps-1", "PS5", "ps"),
            ]);
        var card = new RateCardBuilder()
            .AddBase("pcg", PlayMode.Offline, 1, 80_000)
            .AddBase("ps", PlayMode.Offline, 1, 110_000)
            .Build();
        var start = T.At(2026, 6, 2, 10, 0);
        var pause = new PauseInterval(start.AddMinutes(2), start.AddMinutes(3));
        var moved = new Session("moved", [
            new PlaySegment("a", start, start.AddMinutes(10), "ps-1", "ps", PlayMode.Offline, 1),
            new PlaySegment("b", start.AddMinutes(10), start.AddMinutes(20), "pc-1", "pcg", PlayMode.Offline, 1),
        ], [pause]);
        var priced = SessionPricer.Price(moved, card, stations: stations);
        Assert.Equal(19, priced.TotalBilledMinutes);

        var unknown = new Session("u", [
            new PlaySegment("a", start, start.AddMinutes(10), "zz", "ps", PlayMode.Offline, 1),
        ], [pause]);
        var error = Assert.Throws<BillingException>(() => SessionPricer.Price(unknown, card, stations: stations));
        Assert.Equal(BillingErrors.InvalidSession, error.Code);

        var bare = new Session("b", [
            new PlaySegment("a", start, start.AddMinutes(10), "zz", "ps", PlayMode.Offline, 1),
        ], [pause]);
        var bareError = Assert.Throws<BillingException>(() => SessionPricer.Price(bare, card));
        Assert.Equal(BillingErrors.InvalidSession, bareError.Code);
    }

    [Fact]
    public void Gift_share_of_a_large_pocket_does_not_make_real_credit_negative()
    {
        var pocket = new WalletPocket(5_000_000_000, 5_000_000_000);
        Assert.True(pocket.TrySpend(3_000_000_000, DebtPolicy.None, out var once, out var gift1, out var real1));
        Assert.True(once.TrySpend(3_000_000_000, DebtPolicy.None, out var twice, out var gift2, out var real2));
        Assert.Equal(1_500_000_000, gift1);
        Assert.Equal(1_500_000_000, real1);
        Assert.Equal(1_500_000_000, gift2);
        Assert.Equal(1_500_000_000, real2);
        Assert.Equal(2_000_000_000, twice.GiftToman);
        Assert.Equal(2_000_000_000, twice.RealToman);
        Assert.True(twice.RealToman >= 0);
    }

    [Fact]
    public void An_unknown_payment_method_has_its_own_error_code()
    {
        var result = PaymentAllocator.Allocate(
            [new PayableLine(null, false, true, 1_000)],
            [new Tender((PaymentMethod)9, 1_000)],
            RoundingPolicy.Off);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == PaymentErrors.UnknownMethod);
    }

    private static CustomerAccount Account(long balance)
        => new("m", WalletSettings.Shared with { ApplyDiscountsToPc = true }, new WalletPocket(balance, 0), new WalletPocket(0, 0), []);

    private static SessionCharge Play(RateCard card, string group, DateTimeOffset start, DateTimeOffset end)
        => SessionPricer.Price(new Session("s", [
            new PlaySegment("a", start, end, "st", group, PlayMode.Offline, 1, StationKind.Pc),
        ]), card);
}
