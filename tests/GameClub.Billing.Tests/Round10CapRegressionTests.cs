using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class Round10CapRegressionTests
{
    static RateCard Flat() => new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();

    static SessionCharge HourCharge()
    {
        var s = T.At(2026, 6, 2, 10, 0);
        return SessionPricer.Price(
            new Session("x", [new PlaySegment("a", s, s.AddHours(1), "st", "g", PlayMode.Offline, 1)]),
            Flat());
    }

    static CustomerAccount Cust(long money)
        => new("m", WalletSettings.Shared with { ApplyDiscountsToPc = true }, new WalletPocket(money, 0), new WalletPocket(0, 0), []);

    [Fact]
    public void rc_219_club_cap_below_100_is_not_saturation()
    {
        var charge = HourCharge();
        Assert.Equal(46_200, AccountSettlement.Settle(
            charge,
            new PcAccount("m", 1_000_000, []),
            discount: new SessionDiscount(LevelPercent: 58, Cap: 53)).MoneyConsumedToman);
        Assert.Equal(27_500, AccountSettlement.Settle(
            charge,
            new PcAccount("m", 1_000_000, []),
            discount: new SessionDiscount(LevelPercent: 41, CodePercent: 34, Cap: 58)).MoneyConsumedToman);
        Assert.Equal(19_800, AccountSettlement.Settle(
            charge,
            new PcAccount("m", 1_000_000, []),
            discount: new SessionDiscount(CodePercent: 82, Cap: 54)).MoneyConsumedToman);

        var bd = JalaliDate.FromGregorian(new LocalDate(2026, 6, 2));
        var pol = new DiscountPolicy(60, BirthdayWindow.ExactDay);
        var issued = T.At(2026, 6, 2, 11, 0);
        Assert.Equal(44_000, CustomerPlay.Settle(
            charge,
            Cust(1_000_000),
            pricing: new CustomerPricing("Titan", 28, bd, 40),
            discounts: pol,
            issuedAt: issued).Settlement.MoneyConsumedToman);
        Assert.Equal(35_200, CustomerPlay.Settle(
            charge,
            Cust(1_000_000),
            pricing: new CustomerPricing("Titan", 28, null, 0),
            code: DiscountCode.PercentOff("P", 40),
            discounts: pol,
            issuedAt: issued).Settlement.MoneyConsumedToman);
    }
}
