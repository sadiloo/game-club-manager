using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class GraceRetain500Tests
{
    [Fact]
    public void positive_500_80k_grace_retains_balance_zero_paid()
    {
        var start = T.At(2026, 6, 2, 10, 0);
        var end = T.At(2026, 6, 2, 10, 1);
        var charge = new SessionCharge(
            "s1",
            [
                new PricedSlice(
                    "a",
                    "pc-1",
                    PilotSeed.PcNormalGroupId,
                    PlayMode.Offline,
                    1,
                    start,
                    end,
                    60,
                    1,
                    80_000,
                    RateSource.BaseTariff,
                    null,
                    1_333),
            ],
            60,
            1,
            1_333);
        var account = new CustomerAccount(
            "c1",
            WalletSettings.Shared,
            new WalletPocket(500, 0),
            new WalletPocket(0, 0),
            []);
        var result = CustomerPlay.Settle(charge, account);
        Assert.Equal(0, result.Settlement.MoneyConsumedToman);
        Assert.Equal(500, result.Account.Pc.TotalToman);
        Assert.NotNull(result.Settlement.Grace);
        Assert.True(result.Settlement.Grace!.WaivedToman > 0);
    }
}
