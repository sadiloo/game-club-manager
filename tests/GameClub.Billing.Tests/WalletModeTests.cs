using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class WalletModeTests
{
    private static readonly DateTimeOffset At = T.At(2026, 6, 2, 10, 0);

    [Fact]
    public void Shared_wallet_debt_blocks_pc_unless_package_minutes_are_explicitly_allowed()
    {
        var blocked = new CustomerAccount(
            "m",
            WalletSettings.Shared,
            new WalletPocket(30_000, 0),
            new WalletPocket(0, 0),
            [new TimePackage("hours", 120, 200_000)]);
        var inDebt = blocked.ChargeNonPc(80_000);
        Assert.True(inDebt.Accepted);
        Assert.Equal(-50_000, inDebt.Account.PcMoneyToman);
        Assert.Equal(50_000, inDebt.Account.Pc.DebtToman);

        var estimate = CustomerPlay.Estimate(inDebt.Account, CafeFixture.ExampleNormal, PlayMode.Offline, 1, At, CafeFixture.Card());
        Assert.True(estimate.BlockedByDebt);
        Assert.Equal(0, estimate.TotalMinutes);

        var play = CustomerPlay.Settle(PcHour(), inDebt.Account);
        Assert.Equal(0, play.Settlement.PackageMinutesConsumed);
        Assert.Equal(110_000, play.Settlement.UncoveredToman);
        Assert.Equal(-50_000, play.Account.PcMoneyToman);
        Assert.Equal(120, play.Account.Packages[0].RemainingMinutes);

        var allowed = blocked with { Settings = new WalletSettings(WalletMode.Shared, allowPcWhileInDebt: true) };
        var allowedDebt = allowed.ChargeNonPc(80_000).Account;
        var packageEstimate = CustomerPlay.Estimate(allowedDebt, CafeFixture.ExampleNormal, PlayMode.Offline, 1, At, CafeFixture.Card());
        Assert.False(packageEstimate.BlockedByDebt);
        Assert.Equal(120, packageEstimate.EligiblePackageMinutes);
        Assert.Equal(0, packageEstimate.MoneyMinutes);
        Assert.Equal(120, packageEstimate.TotalMinutes);
        Assert.Equal(-50_000, packageEstimate.MoneyBalanceToman);

        var onPackage = CustomerPlay.Settle(PcHour(), allowedDebt);
        Assert.Equal(60, onPackage.Settlement.PackageMinutesConsumed);
        Assert.Equal(0, onPackage.Settlement.MoneyConsumedToman);
        Assert.Equal(0, onPackage.Settlement.UncoveredToman);
        Assert.Equal(-50_000, onPackage.Account.PcMoneyToman);

        var noPackage = allowedDebt with { Packages = [] };
        var stillNegative = CustomerPlay.Settle(PcHour(), noPackage);
        Assert.Equal(110_000, stillNegative.Settlement.UncoveredToman);
        Assert.Equal(-50_000, stillNegative.Account.PcMoneyToman);

        var limited = blocked with { Settings = WalletSettings.Shared.WithDebt(DebtPolicy.Limited(40_000)) };
        var refused = limited.ChargeNonPc(80_000);
        Assert.False(refused.Accepted);
        Assert.Equal(30_000, refused.Account.PcMoneyToman);
    }

    [Fact]
    public void Separate_balances_keep_console_debt_off_the_pc_balance_and_top_ups_name_a_target()
    {
        var account = new CustomerAccount(
            "m",
            WalletSettings.Separate,
            new WalletPocket(50_000, 0),
            new WalletPocket(30_000, 0),
            []);
        var console = account.ChargeNonPc(80_000);
        Assert.True(console.Accepted);
        Assert.Equal(50_000, console.Account.PcMoneyToman);
        Assert.Equal(-50_000, console.Account.NonPcMoneyToman);

        var estimate = CustomerPlay.Estimate(console.Account, CafeFixture.ExampleNormal, PlayMode.Offline, 1, At, CafeFixture.Card());
        Assert.False(estimate.BlockedByDebt);
        Assert.Equal(27, estimate.MoneyMinutes);
        Assert.Equal(27, estimate.TotalMinutes);

        var pc = CustomerPlay.Settle(PcHour(), console.Account);
        var through = MoneyMath.ChargeForMinutes(CafeFixture.ExampleNormalOnePlayerHourly, 28, RateRemainderMode.Nearest);
        Assert.Equal(50_000, pc.Settlement.MoneyConsumedToman);
        Assert.Equal(through - 50_000, pc.Settlement.Grace!.WaivedToman);
        Assert.Equal(110_000 - through, pc.Settlement.UncoveredToman);
        Assert.Equal(0, pc.Account.PcMoneyToman);
        Assert.Equal(-50_000, pc.Account.NonPcMoneyToman);

        var topped = pc.Account.TopUp(TopUpTarget.NonPc, 50_000).TopUp(TopUpTarget.Pc, 10_000);
        Assert.Equal(10_000, topped.PcMoneyToman);
        Assert.Equal(0, topped.NonPcMoneyToman);

        var wrong = Assert.Throws<BillingException>(() => account.TopUp(TopUpTarget.Shared, 1_000));
        Assert.Equal("invalid_topup", wrong.Code);
        var sharedOnly = Assert.Throws<BillingException>(() => blockedShared().TopUp(TopUpTarget.Pc, 1_000));
        Assert.Equal("invalid_topup", sharedOnly.Code);
        var separateFlag = Assert.Throws<BillingException>(() => new WalletSettings(WalletMode.Separate, allowPcWhileInDebt: true));
        Assert.Equal("invalid_policy", separateFlag.Code);
    }

    private static CustomerAccount blockedShared()
        => new("m", WalletSettings.Shared, new WalletPocket(0, 0), new WalletPocket(0, 0), []);

    private static SessionCharge PcHour()
        => CafeFixture.Price(CafeFixture.Session(
            "pc",
            CafeFixture.Play("a", At, At.AddHours(1), "example-pc-1", CafeFixture.ExampleNormal, 1)));
}
