using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class Round5Tests
{
    [Fact]
    public void A_group_limited_code_discounts_that_pc_group()
    {
        var card = PilotSeed.CreateCard();
        var vip = PilotSeed.PcVipGroupId;
        var start = T.At(2026, 6, 2, 10, 0);
        var charge = Play(card, vip, start, start.AddHours(1));
        var percent = CustomerPlay.Settle(charge, Account(), code: DiscountCode.PercentOff("VIP10", 10, allowedGroupIds: new[] { vip }));
        Assert.Equal(11_000, percent.Settlement.DiscountToman);
        var fixedCode = new DiscountCode("VIPF", 0, 5_000, null, null, new[] { vip }, null);
        var money = CustomerPlay.Settle(charge, Account(), code: fixedCode);
        Assert.Equal(5_000, money.Settlement.DiscountToman);
    }

    [Fact]
    public void A_flat_rate_across_midnight_discounts_each_minute_date()
    {
        var card = new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();
        var saturday = DiscountCode.PercentOff("SAT", 50, allowedWeekdays: new[] { DayOfWeek.Saturday });
        var friday = T.At(2026, 6, 5, 23, 0);
        var fromFriday = CustomerPlay.Settle(Play(card, "g", friday, friday.AddHours(2)), Account(), code: saturday, issuedAt: friday.AddHours(2));
        Assert.Equal(55_000, fromFriday.Settlement.DiscountToman);
        var saturdayNight = T.At(2026, 6, 6, 23, 0);
        var fromSaturday = CustomerPlay.Settle(Play(card, "g", saturdayNight, saturdayNight.AddHours(2)), Account(), code: saturday, issuedAt: saturdayNight.AddHours(2));
        Assert.Equal(55_000, fromSaturday.Settlement.DiscountToman);

        var birth = JalaliDate.FromGregorian(new LocalDate(2026, 6, 6));
        var birthday = CustomerPlay.Settle(
            Play(card, "g", friday, friday.AddHours(2)),
            Account(),
            pricing: new CustomerPricing("B", 0, birth, 20),
            issuedAt: friday.AddHours(2));
        Assert.Equal(22_000, birthday.Settlement.DiscountToman);
    }

    [Fact]
    public void Gift_covers_pc_time_when_real_credit_is_already_negative()
    {
        var pocket = new WalletPocket(-1_000, 5_000);
        Assert.True(pocket.TrySpend(2_000, DebtPolicy.None, out var updated, out var gift, out var real));
        Assert.Equal(2_000, gift);
        Assert.Equal(0, real);
        Assert.Equal(-1_000, updated.RealToman);
        Assert.Equal(3_000, updated.GiftToman);

        var account = new CustomerAccount("m", WalletSettings.Shared, new WalletPocket(-1_000, 5_000), new WalletPocket(0, 0), []);
        var start = T.At(2026, 6, 2, 10, 0);
        var play = CustomerPlay.Settle(
            Play(PilotSeed.CreateCard(), PilotSeed.PcNormalGroupId, start, start.AddMinutes(1)),
            account);
        Assert.True(play.Settlement.MoneyConsumedToman > 0);
        Assert.Equal(play.Settlement.MoneyConsumedToman, play.GiftDrawnToman);
        Assert.Equal(0, play.RealDrawnToman);
        Assert.Equal(-1_000, play.Account.Pc.RealToman);
    }

    [Fact]
    public void Block_overshoot_spends_the_kept_balance_before_write_off()
    {
        var policy = new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var start = T.At(2026, 6, 2, 10, 0);
        var played = SessionPricer.Price(new Session("s", [
            new PlaySegment("a", start, start.AddMinutes(8), "pc-1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, StationKind.Pc),
        ]), PilotSeed.CreateCard(), policy: policy);
        var account = new CustomerAccount("m", WalletSettings.Shared, new WalletPocket(5_000, 0), new WalletPocket(0, 0), []);
        var play = CustomerPlay.Settle(played, account, billing: policy);
        Assert.Equal(5_000, play.Settlement.MoneyConsumedToman);
        Assert.Equal(0, play.Account.PcMoneyToman);
        Assert.True(play.Settlement.WriteOffToman > 0);
        Assert.Equal("pc_overshoot", play.Settlement.Alert!.Code);
    }

    [Fact]
    public void A_zero_balance_does_not_grant_a_free_block_grace_minute()
    {
        var policy = new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var start = T.At(2026, 6, 2, 10, 0);
        var plan = PrepaidPlanner.ForAmount(start, 0, "a", "pc-1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, PilotSeed.CreateCard(), policy: policy);
        Assert.Null(plan.Grace);
        var played = SessionPricer.Price(new Session("s", [
            new PlaySegment("a", start, start.AddMinutes(5), "pc-1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, StationKind.Pc),
        ]), PilotSeed.CreateCard(), policy: policy);
        var settled = AccountSettlement.Settle(played, new PcAccount("m", 0, []), billing: policy);
        Assert.Null(settled.Grace);
        Assert.Equal(0, settled.MoneyConsumedToman);
        Assert.True(settled.WriteOffToman > 0);
    }

    [Fact]
    public void A_code_window_is_applied_per_minute()
    {
        var card = PilotSeed.CreateCard();
        var vip = PilotSeed.PcVipGroupId;
        var start = T.At(2026, 6, 2, 23, 0);
        var untilMidnight = DiscountCode.PercentOff("EARLY", 50, validUntil: T.At(2026, 6, 3, 0, 0));
        var fromMidnight = DiscountCode.PercentOff("LATE", 50, validFrom: T.At(2026, 6, 3, 0, 0));
        var early = CustomerPlay.Settle(Play(card, vip, start, start.AddHours(2)), Account(), code: untilMidnight, issuedAt: start.AddHours(2));
        var late = CustomerPlay.Settle(Play(card, vip, start, start.AddHours(2)), Account(), code: fromMidnight, issuedAt: start.AddHours(2));
        Assert.Equal(55_000, early.Settlement.DiscountToman);
        Assert.Equal(65_000, late.Settlement.DiscountToman);
    }

    [Fact]
    public void A_kind_conflict_is_rejected_and_a_zero_length_pause_is_ignored()
    {
        var stations = new StationDirectory(
            [new StationGroup(PilotSeed.PcNormalGroupId, "PC", StationKind.Pc)],
            [new Station("pc-1", "PC", PilotSeed.PcNormalGroupId)]);
        var start = T.At(2026, 6, 2, 10, 0);
        var conflict = new Session("c", [
            new PlaySegment("a", start, start.AddMinutes(10), "pc-1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, StationKind.Console),
        ], [new PauseInterval(start.AddMinutes(1), start.AddMinutes(2))]);
        var error = Assert.Throws<BillingException>(() => SessionPricer.Price(conflict, PilotSeed.CreateCard(), stations: stations));
        Assert.Equal(BillingErrors.InvalidSession, error.Code);

        var zero = new Session("z", [
            new PlaySegment("a", start, start.AddMinutes(10), "pc-1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, StationKind.Pc),
        ], [new PauseInterval(start.AddMinutes(2), start.AddMinutes(2))]);
        var priced = SessionPricer.Price(zero, PilotSeed.CreateCard(), stations: stations);
        Assert.Equal(10, priced.TotalBilledMinutes);
    }

    private static CustomerAccount Account()
        => new("m", WalletSettings.Shared with { ApplyDiscountsToPc = true }, new WalletPocket(1_000_000, 0), new WalletPocket(0, 0), []);

    private static SessionCharge Play(RateCard card, string group, DateTimeOffset start, DateTimeOffset end)
        => SessionPricer.Price(new Session("s", [
            new PlaySegment("a", start, end, "st", group, PlayMode.Offline, 1, StationKind.Pc),
        ]), card);
}
