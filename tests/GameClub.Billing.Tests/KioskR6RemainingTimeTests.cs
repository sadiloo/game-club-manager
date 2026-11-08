using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class KioskR6RemainingTimeTests
{
    [Fact]
    public void default_grace_does_not_add_prospective_minute_below_sixty_funded_minutes()
    {
        const string group = "r6-80k";
        var at = T.At(2026, 6, 3, 14, 0);
        var account = new PcAccount("c", 48_667, []);
        var card = new RateCardBuilder()
            .AddBase(group, PlayMode.Offline, 1, 80_000)
            .Build();
        var estimate = RemainingTimeEstimator.Estimate(
            account,
            group,
            PlayMode.Offline,
            1,
            at,
            card,
            graceMinuteEnabled: true);
        Assert.Equal(36, estimate.MoneyMinutes);
    }
}
