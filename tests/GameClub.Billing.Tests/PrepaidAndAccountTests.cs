using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class PrepaidAndAccountTests
{
    [Fact]
    public void Amount_limit_crosses_the_special_hour_boundary_without_overspending()
    {
        var exact = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 10, 0),
            110_000,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        Assert.Equal(T.At(2026, 6, 2, 11, 0), exact.End);
        Assert.Equal(110_000, exact.ConsumedToman);
        Assert.Equal(0, exact.UnusedToman);
        Assert.Equal(exact.Lines.Sum(line => line.AmountToman), exact.ConsumedToman);

        // 08:00–08:30 at 160,000 costs 80,000. The remaining 80,000 buys 43 minutes at 110,000
        // (44th minute rounds to 80,667). End is 09:13. Unused 1,167 cannot buy another minute.
        var crossing = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 8, 0),
            160_000,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        Assert.Equal(T.At(2026, 6, 2, 9, 13), crossing.End);
        Assert.Equal(2, crossing.Lines.Count);
        Assert.Equal(80_000, crossing.Lines[0].AmountToman);
        Assert.Equal(RateSource.TimeBand, crossing.Lines[0].Source);
        Assert.Equal(43, crossing.Lines[1].BilledMinutes);
        Assert.Equal(78_833, crossing.Lines[1].AmountToman);
        Assert.Equal(158_833, crossing.ConsumedToman);
        Assert.Equal(1_167, crossing.UnusedToman);
        Assert.True(crossing.ConsumedToman <= 160_000);

        var tooSmall = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 10, 0),
            1_000,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        Assert.Equal(tooSmall.Start, tooSmall.End);
        Assert.Equal(0, tooSmall.ConsumedToman);
        Assert.Equal(1_000, tooSmall.UnusedToman);
    }

    [Fact]
    public void Duration_limit_ends_on_the_clock_and_still_splits_bands()
    {
        var plan = PrepaidPlanner.ForDuration(
            T.At(2026, 6, 2, 23, 0),
            TimeSpan.FromMinutes(90),
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card());

        Assert.Equal(T.At(2026, 6, 3, 0, 30), plan.End);
        Assert.Equal(110_000, plan.Lines[0].AmountToman);
        Assert.Equal(80_000, plan.Lines[1].AmountToman);
        Assert.Equal(190_000, plan.ConsumedToman);
    }

    [Fact]
    public void Package_is_consumed_first_then_money_and_estimates_depend_on_the_group()
    {
        var package = new TimePackage("two-hours", 120, 200_000);
        var account = new PcAccount("member-1", 220_000, [package]);
        var at = T.At(2026, 6, 2, 18, 0);

        var beforeNormal = RemainingTimeEstimator.Estimate(account, CafeFixture.Normal, PlayMode.Offline, 1, at, CafeFixture.Card());
        var beforeVip = RemainingTimeEstimator.Estimate(account, CafeFixture.Vip, PlayMode.Offline, 1, at, CafeFixture.Card());
        Assert.Equal(120, beforeNormal.EligiblePackageMinutes);
        Assert.Equal(120, beforeNormal.MoneyMinutes);
        Assert.Equal(240, beforeNormal.TotalMinutes);
        Assert.Equal(60, beforeVip.MoneyMinutes);
        Assert.Equal(180, beforeVip.TotalMinutes);
        Assert.True(beforeNormal.TotalMinutes > beforeVip.TotalMinutes);

        var firstHour = Settle(account, T.At(2026, 6, 2, 18, 0), T.At(2026, 6, 2, 19, 0));
        Assert.Equal(60, firstHour.PackageMinutesConsumed);
        Assert.Equal(0, firstHour.MoneyConsumedToman);
        Assert.Equal(0, firstHour.UncoveredToman);
        Assert.Equal(220_000, firstHour.UpdatedAccount.MoneyBalanceToman);
        Assert.Equal(60, firstHour.UpdatedAccount.Packages[0].RemainingMinutes);
        Assert.Equal(220_000, firstHour.GrossToman);

        var nextDay = Settle(firstHour.UpdatedAccount, T.At(2026, 6, 3, 18, 0), T.At(2026, 6, 3, 19, 0));
        Assert.Equal(60, nextDay.PackageMinutesConsumed);
        Assert.Equal(0, nextDay.MoneyConsumedToman);
        Assert.Equal(0, nextDay.UpdatedAccount.Packages[0].RemainingMinutes);
        Assert.Equal(220_000, nextDay.UpdatedAccount.MoneyBalanceToman);

        var fallback = Settle(nextDay.UpdatedAccount, T.At(2026, 6, 3, 20, 0), T.At(2026, 6, 3, 20, 30));
        Assert.Equal(0, fallback.PackageMinutesConsumed);
        Assert.Equal(110_000, fallback.MoneyConsumedToman);
        Assert.Equal(0, fallback.UncoveredToman);
        Assert.Equal(110_000, fallback.UpdatedAccount.MoneyBalanceToman);

        var after = T.At(2026, 6, 3, 20, 30);
        var normalLeft = RemainingTimeEstimator.Estimate(fallback.UpdatedAccount, CafeFixture.Normal, PlayMode.Offline, 1, after, CafeFixture.Card());
        var vipLeft = RemainingTimeEstimator.Estimate(fallback.UpdatedAccount, CafeFixture.Vip, PlayMode.Offline, 1, after, CafeFixture.Card());
        Assert.Equal(0, normalLeft.EligiblePackageMinutes);
        Assert.Equal(60, normalLeft.MoneyMinutes);
        Assert.Equal(30, vipLeft.MoneyMinutes);
        Assert.Equal(60, normalLeft.TotalMinutes);
        Assert.Equal(30, vipLeft.TotalMinutes);
    }

    [Fact]
    public void Money_first_leaves_the_package_intact_when_the_balance_covers_the_session()
    {
        var account = new PcAccount("member-1", 220_000, [new TimePackage("two-hours", 120, 200_000)]);
        var charge = CafeFixture.Price(CafeFixture.Session(
            "vip",
            CafeFixture.Play("a", T.At(2026, 6, 2, 18, 0), T.At(2026, 6, 2, 19, 0), "vip-1", CafeFixture.Vip, 1)));
        var settled = AccountSettlement.Settle(charge, account, new AccountPolicy(ConsumptionOrder.MoneyThenPackages));

        Assert.Equal(220_000, settled.MoneyConsumedToman);
        Assert.Equal(0, settled.PackageMinutesConsumed);
        Assert.Equal(0, settled.UpdatedAccount.MoneyBalanceToman);
        Assert.Equal(120, settled.UpdatedAccount.Packages[0].RemainingMinutes);
    }

    [Fact]
    public void Package_group_window_and_expiry_limit_what_it_can_cover()
    {
        var windowed = new TimePackage(
            "night",
            1_000,
            null,
            null,
            new TimeOnly(0, 0),
            new TimeOnly(8, 30));
        var account = new PcAccount("member-1", 1_000_000, [windowed]);
        var charge = CafeFixture.Price(CafeFixture.Session(
            "pc",
            CafeFixture.Play("a", T.At(2026, 6, 2, 7, 0), T.At(2026, 6, 2, 9, 0), "pc-1", CafeFixture.Normal, 1)));
        Assert.Equal(220_000, charge.TotalAmountToman);

        var settled = AccountSettlement.Settle(charge, account);
        Assert.Equal(90, settled.PackageMinutesConsumed);
        Assert.Equal(55_000, settled.MoneyConsumedToman);
        Assert.Equal(910, settled.UpdatedAccount.Packages[0].RemainingMinutes);
        Assert.Equal(settled.GrossToman, settled.PackageCoveredToman + settled.MoneyConsumedToman + settled.UncoveredToman);

        var vipOnly = new PcAccount(
            "member-2",
            110_000,
            [new TimePackage("vip-pack", 60, null, new HashSet<string> { CafeFixture.Vip })]);
        var onNormal = AccountSettlement.Settle(
            CafeFixture.Price(CafeFixture.Session(
                "pc",
                CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0), "pc-1", CafeFixture.Normal, 1))),
            vipOnly);
        Assert.Equal(0, onNormal.PackageMinutesConsumed);
        Assert.Equal(110_000, onNormal.MoneyConsumedToman);
        Assert.Equal(60, onNormal.UpdatedAccount.Packages[0].RemainingMinutes);

        var expired = new PcAccount(
            "member-3",
            110_000,
            [new TimePackage("old", 60, null, null, null, null, new DateOnly(2026, 6, 2))]);
        var nextDay = AccountSettlement.Settle(
            CafeFixture.Price(CafeFixture.Session(
                "pc",
                CafeFixture.Play("a", T.At(2026, 6, 3, 10, 0), T.At(2026, 6, 3, 11, 0), "pc-1", CafeFixture.Normal, 1))),
            expired);
        Assert.Equal(0, nextDay.PackageMinutesConsumed);
        Assert.Equal(110_000, nextDay.MoneyConsumedToman);

        var stillValid = AccountSettlement.Settle(
            CafeFixture.Price(CafeFixture.Session(
                "pc",
                CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0), "pc-1", CafeFixture.Normal, 1))),
            expired);
        Assert.Equal(60, stillValid.PackageMinutesConsumed);
        Assert.Equal(0, stillValid.MoneyConsumedToman);
    }

    [Fact]
    public void Shortfall_does_not_drive_the_balance_negative()
    {
        var account = new PcAccount("member-1", 50_000, []);
        var settled = Settle(account, T.At(2026, 6, 2, 18, 0), T.At(2026, 6, 2, 19, 0));
        Assert.Equal(50_000, settled.MoneyConsumedToman);
        Assert.Equal(170_000, settled.UncoveredToman);
        Assert.Equal(0, settled.UpdatedAccount.MoneyBalanceToman);
        Assert.Equal(settled.GrossToman, settled.PackageCoveredToman + settled.MoneyConsumedToman + settled.UncoveredToman);
    }

    private static Settlement Settle(PcAccount account, DateTimeOffset start, DateTimeOffset end)
    {
        var charge = CafeFixture.Price(CafeFixture.Session(
            "vip",
            CafeFixture.Play("a", start, end, "vip-1", CafeFixture.Vip, 1)));
        return AccountSettlement.Settle(charge, account);
    }
}
