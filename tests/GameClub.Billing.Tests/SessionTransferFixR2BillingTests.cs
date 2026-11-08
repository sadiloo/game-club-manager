using GameClub.Billing;

namespace GameClub.Billing.Tests;

/// <summary>SS-4 fix round 2 — multiple fractional hops must not lose a billable minute (acceptance 9–10).</summary>
public sealed class SessionTransferFixR2BillingTests
{
    private const long Qa600Hourly = 600_000;
    private const string Qa600Group = "qa600";

    private static RateCard Qa600Card()
        => new RateCardBuilder()
            .AddBase(Qa600Group, PlayMode.Offline, 1, Qa600Hourly)
            .Build();

    private static DateTimeOffset FractionalEnd(DateTimeOffset start, double seconds)
        => start.AddTicks((long)(seconds * TimeSpan.TicksPerSecond));

    [Fact]
    public void equal_tariff_two_fractional_hops_at_30_75_and_60_75_bill_like_unsplit_121_5s_at_600000_per_hour()
    {
        var card = Qa600Card();
        var policy = BillingPolicy.Default;
        var start = T.At(2026, 11, 9, 14, 0);
        var hop1 = FractionalEnd(start, 30.75);
        var hop2 = FractionalEnd(start, 60.75);
        var end = FractionalEnd(start, 121.5);

        var unsplit = SessionPricer.Price(
            CafeFixture.Session(
                "unsplit",
                CafeFixture.Play("a", start, end, "frac-a", Qa600Group, 1)),
            card,
            policy: policy);
        var split = SessionPricer.Price(
            CafeFixture.Session(
                "split",
                CafeFixture.Play("a", start, hop1, "frac-a", Qa600Group, 1),
                CafeFixture.Play("b", hop1, hop2, "frac-b", Qa600Group, 1),
                CafeFixture.Play("c", hop2, end, "frac-c", Qa600Group, 1)),
            card,
            policy: policy);

        const long expectedMinutes = 3;
        var oracleAmount = MoneyMath.ChargeForMinutes(Qa600Hourly, expectedMinutes, policy.Remainder);

        Assert.Equal(expectedMinutes, unsplit.TotalBilledMinutes);
        Assert.Equal(oracleAmount, unsplit.TotalAmountToman);
        Assert.Equal(30_000, unsplit.TotalAmountToman);
        Assert.Equal(unsplit.TotalBilledMinutes, split.TotalBilledMinutes);
        Assert.Equal(unsplit.TotalAmountToman, split.TotalAmountToman);
        Assert.Equal(30_000, split.TotalAmountToman);
        Assert.Equal(3, split.TotalBilledMinutes);
    }
}
