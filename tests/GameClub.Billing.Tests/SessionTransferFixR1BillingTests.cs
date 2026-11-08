using GameClub.Billing;

namespace GameClub.Billing.Tests;

/// <summary>SS-4 fix round 1 — fractional hop must match unsplit billable minutes (acceptance 10).</summary>
public sealed class SessionTransferFixR1BillingTests
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
    public void equal_tariff_fractional_hop_30_75s_plus_30_75s_bills_like_unsplit_61_5s_at_600000_per_hour()
    {
        var card = Qa600Card();
        var policy = BillingPolicy.Default;
        var start = T.At(2026, 11, 9, 14, 0);
        var hop = FractionalEnd(start, 30.75);
        var end = FractionalEnd(start, 61.5);

        var unsplit = SessionPricer.Price(
            CafeFixture.Session(
                "unsplit",
                CafeFixture.Play("a", start, end, "frac-a", Qa600Group, 1)),
            card,
            policy: policy);
        var split = SessionPricer.Price(
            CafeFixture.Session(
                "split",
                CafeFixture.Play("a", start, hop, "frac-a", Qa600Group, 1),
                CafeFixture.Play("b", hop, end, "frac-b", Qa600Group, 1)),
            card,
            policy: policy);

        const long expectedMinutes = 2;
        var oracleAmount = MoneyMath.ChargeForMinutes(Qa600Hourly, expectedMinutes, policy.Remainder);

        Assert.Equal(expectedMinutes, unsplit.TotalBilledMinutes);
        Assert.Equal(oracleAmount, unsplit.TotalAmountToman);
        Assert.Equal(unsplit.TotalBilledMinutes, split.TotalBilledMinutes);
        Assert.Equal(unsplit.TotalAmountToman, split.TotalAmountToman);
        Assert.Equal(20_000, split.TotalAmountToman);
    }

    [Fact]
    public void whole_second_equal_tariff_hops_still_match_unsplit_oracle()
    {
        var policy = BillingPolicy.Default;
        var start = T.At(2026, 6, 3, 14, 0);
        var thirty = T.At(2026, 6, 3, 14, 0, 30);
        var sixty = T.At(2026, 6, 3, 14, 1, 0);
        var twoMin = T.At(2026, 6, 3, 14, 2, 0);

        var hop30 = SessionPricer.Price(
            CafeFixture.Session(
                "30",
                CafeFixture.Play("a", start, thirty, "ps5-1", CafeFixture.Ps5, 1),
                CafeFixture.Play("b", thirty, sixty, "ps5-2", CafeFixture.Ps5, 1)),
            CafeFixture.Card(),
            policy: policy);
        var whole30 = SessionPricer.Price(
            CafeFixture.Session("30w", CafeFixture.Play("a", start, sixty, "ps5-1", CafeFixture.Ps5, 1)),
            CafeFixture.Card(),
            policy: policy);
        Assert.Equal(whole30.TotalBilledMinutes, hop30.TotalBilledMinutes);
        Assert.Equal(whole30.TotalAmountToman, hop30.TotalAmountToman);

        var ninety = T.At(2026, 6, 3, 14, 1, 30);
        var hop90 = SessionPricer.Price(
            CafeFixture.Session(
                "90",
                CafeFixture.Play("a", start, sixty, "ps5-1", CafeFixture.Ps5, 1),
                CafeFixture.Play("b", sixty, twoMin, "ps5-2", CafeFixture.Ps5, 1)),
            CafeFixture.Card(),
            policy: policy);
        var whole120 = SessionPricer.Price(
            CafeFixture.Session("120", CafeFixture.Play("a", start, twoMin, "ps5-1", CafeFixture.Ps5, 1)),
            CafeFixture.Card(),
            policy: policy);
        Assert.Equal(whole120.TotalBilledMinutes, hop90.TotalBilledMinutes);
        Assert.Equal(whole120.TotalAmountToman, hop90.TotalAmountToman);
    }
}
