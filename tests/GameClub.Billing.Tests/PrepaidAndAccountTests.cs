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
        // (78,833). The leftover 1,167 is below the marginal 44th minute (1,834), so grace
        // folds that minute into the same line and the end is still 09:14. Waived is 667.
        var crossing = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 8, 0),
            160_000,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        Assert.Equal(T.At(2026, 6, 2, 9, 14), crossing.End);
        Assert.Equal(2, crossing.Lines.Count);
        Assert.Equal(80_000, crossing.Lines[0].AmountToman);
        Assert.Equal(RateSource.TimeBand, crossing.Lines[0].Source);
        Assert.Equal(44, crossing.Lines[1].BilledMinutes);
        Assert.Equal(MoneyMath.ChargeForMinutes(110_000, 44, RateRemainderMode.Nearest), crossing.Lines[1].AmountToman);
        Assert.Equal(158_833, crossing.ConsumedToman);
        Assert.Equal(1_167, crossing.UnusedToman);
        Assert.Equal(1, crossing.Grace!.Minutes);
        Assert.Equal(1_834, crossing.Grace.WaivedToman);

        var tooSmall = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 10, 0),
            1_000,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        var minute = MoneyMath.ChargeForMinutes(GameTimerReference.Ps5OnePlayerHourly, 1, RateRemainderMode.Nearest);
        Assert.Equal(tooSmall.Start.AddMinutes(1), tooSmall.End);
        Assert.Equal(0, tooSmall.ConsumedToman);
        Assert.Equal(1_000, tooSmall.UnusedToman);
        Assert.NotNull(tooSmall.Grace);
        Assert.Equal(1, tooSmall.Grace!.Minutes);
        Assert.Equal(minute, tooSmall.Grace.WaivedToman);

        var noGrace = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 10, 0),
            1_000,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card(),
            graceMinuteEnabled: false);
        Assert.Equal(noGrace.Start, noGrace.End);
        Assert.Equal(1_000, noGrace.UnusedToman);
        Assert.Null(noGrace.Grace);
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
    public void Pc_charges_stop_at_zero_and_a_negative_shared_balance_blocks_pc_play()
    {
        var moneyOnly = new PcAccount("member-2", 50_000);
        var stopped = SettlePc(moneyOnly, T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0));
        Assert.Equal(50_000, stopped.MoneyConsumedToman);
        Assert.Equal(1_833, stopped.Grace!.WaivedToman);
        Assert.Equal(1, stopped.Grace.Minutes);
        Assert.Equal(0, stopped.UncoveredToman);
        Assert.Equal(58_167, stopped.WriteOffToman);
        Assert.Equal("pc_overshoot", stopped.Alert!.Code);
        Assert.Equal("write_off", stopped.ReportEntry!.Kind);
        Assert.Equal(0, stopped.UpdatedAccount.MoneyBalanceToman);
        Assert.Equal(0, stopped.DebtToman);
        Assert.Equal(stopped.GrossToman, stopped.MoneyConsumedToman + stopped.Grace.WaivedToman + stopped.WriteOffToman + stopped.UncoveredToman);

        var console = PaymentSplitter.Split(80_000, [
            new Tender(PaymentMethod.Wallet, 80_000, "member-2"),
        ], [new MemberWallet("member-2", 30_000, 0)], DebtPolicy.Unlimited);
        Assert.True(console.IsValid);
        Assert.Equal(-50_000, console.RemainingWallets.Single().BalanceToman);
        Assert.Equal(50_000, console.RemainingWallets.Single().DebtToman);

        var capped = PaymentSplitter.Split(80_000, [
            new Tender(PaymentMethod.Wallet, 80_000, "member-2"),
        ], [new MemberWallet("member-2", 30_000, 0)], DebtPolicy.Limited(40_000));
        Assert.False(capped.IsValid);
        Assert.Contains(capped.Errors, error => error.Code == PaymentErrors.InsufficientWallet);
        Assert.Equal(30_000, capped.RemainingWallets.Single().BalanceToman);

        var inDebt = new PcAccount("member-2", -50_000);
        var blockedEstimate = RemainingTimeEstimator.Estimate(
            inDebt, CafeFixture.ExampleNormal, PlayMode.Offline, 1, T.At(2026, 6, 2, 10, 0), CafeFixture.Card());
        Assert.True(blockedEstimate.BlockedByDebt);
        Assert.Equal(0, blockedEstimate.TotalMinutes);
        Assert.Equal(0, blockedEstimate.MoneyMinutes);

        var blocked = SettlePc(inDebt, T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0));
        Assert.Equal(0, blocked.MoneyConsumedToman);
        Assert.Equal(110_000, blocked.UncoveredToman);
        Assert.Equal(-50_000, blocked.UpdatedAccount.MoneyBalanceToman);
    }

    private static Settlement SettlePc(PcAccount account, DateTimeOffset start, DateTimeOffset end)
    {
        var charge = CafeFixture.Price(CafeFixture.Session(
            "pc",
            CafeFixture.Play("a", start, end, "example-pc-1", CafeFixture.ExampleNormal, 1)));
        return AccountSettlement.Settle(charge, account);
    }

    private static Settlement Settle(PcAccount account, DateTimeOffset start, DateTimeOffset end)
    {
        var charge = CafeFixture.Price(CafeFixture.Session(
            "vip",
            CafeFixture.Play("a", start, end, "example-vip-1", CafeFixture.ExampleVip, 1)));
        return AccountSettlement.Settle(charge, account);
    }
}
