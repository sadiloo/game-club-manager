using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class Ss14GameTariffPrepaidBillingTests
{
  private static long TimerMinutes(PrepaidPlan plan) => plan.GrantedActiveSeconds / 60;

    /// <summary>B3: brief example 9 registration uses ForAmount at 14:00 — 55 minute timer.</summary>
    [Fact]
    public void Ss14B3_Example09_RegistrationForAmount_100k_55Minutes()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var plan = PrepaidPlanner.ForAmount(
            start,
            100_000,
            "a",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        Assert.Equal(55, TimerMinutes(plan));
        Assert.Equal(T.At(2026, 8, 8, 14, 55), plan.End);
    }

    /// <summary>B3: brief example 10 registration — 21 minutes at 37k.</summary>
    [Fact]
    public void Ss14B3_Example10_RegistrationForAmount_37k_21Minutes()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var plan = PrepaidPlanner.ForAmount(
            start,
            37_000,
            "a",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        Assert.Equal(21, TimerMinutes(plan));
    }

    [Fact]
    public void Ss14Example09_PrepaidRecalcAfterGame_46Minutes()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var hop = T.At(2026, 8, 8, 14, 20);
        var session = CafeFixture.Session(
            "s",
            CafeFixture.Play("a", start, hop, "ps5-1", CafeFixture.Ps5, 1));
        var tail = Ss14Fc27Rates.WithGame(CafeFixture.Play("b", hop, hop, "ps5-1", CafeFixture.Ps5, 1));
        var plan = PrepaidPlanner.ForAmountFromSegments(session, hop, tail, 100_000, CafeFixture.Card());
        Assert.Equal(46, TimerMinutes(plan));
        Assert.Equal(T.At(2026, 8, 8, 14, 46), plan.End);
    }

    [Fact]
    public void Ss14Example10_D4_BudgetExhausted_GraceMinute_20Minutes()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var hop = T.At(2026, 8, 8, 14, 19);
        var session = CafeFixture.Session(
            "s",
            CafeFixture.Play("a", start, hop, "ps5-1", CafeFixture.Ps5, 1));
        var tail = Ss14Fc27Rates.WithGame(CafeFixture.Play("b", hop, hop, "ps5-1", CafeFixture.Ps5, 1));
        var plan = PrepaidPlanner.ForAmountFromSegments(session, hop, tail, 37_000, CafeFixture.Card());
        Assert.Equal(20, TimerMinutes(plan));
        Assert.Equal(T.At(2026, 8, 8, 14, 20), plan.End);
    }

    [Fact]
    public void Ss14Example11_D6_MidSessionPrepaid_51Minutes()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var pcHop = T.At(2026, 8, 8, 14, 20);
        var reg = T.At(2026, 8, 8, 14, 30);
        var session = CafeFixture.Session(
            "s",
            CafeFixture.Play("a", start, pcHop, "ps5-1", CafeFixture.Ps5, 1),
            CafeFixture.Play("b", pcHop, reg, "ps5-1", CafeFixture.Ps5, 2) with { SplitReason = "تعداد بازیکن" });
        var tail = session.Segments[^1];
        var plan = PrepaidPlanner.ForAmountFromSegments(session, reg, tail, 100_000, CafeFixture.Card());
        Assert.Equal(51, TimerMinutes(plan));
        Assert.Equal(T.At(2026, 8, 8, 14, 51), plan.End);
    }

    [Fact]
    public void Ss14Example13_PrepaidPlayerCount_51Minutes()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var hop = T.At(2026, 8, 8, 14, 20);
        var session = CafeFixture.Session(
            "s",
            CafeFixture.Play("a", start, hop, "ps5-1", CafeFixture.Ps5, 1),
            CafeFixture.Play("b", hop, hop, "ps5-1", CafeFixture.Ps5, 2) with { SplitReason = "تعداد بازیکن" });
        var tail = session.Segments[^1];
        var plan = PrepaidPlanner.ForAmountFromSegments(session, hop, tail, 100_000, CafeFixture.Card());
        Assert.Equal(51, TimerMinutes(plan));
        Assert.Equal(T.At(2026, 8, 8, 14, 51), plan.End);
    }

    [Fact]
    public void Ss14ForAmountFromSegments_ParityWithForAmount_NoGameSingleSegment()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        const long budget = 100_000;
        var legacy = PrepaidPlanner.ForAmount(
            start,
            budget,
            "a",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        var session = CafeFixture.Session(
            "s",
            CafeFixture.Play("a", start, start, "ps5-1", CafeFixture.Ps5, 1));
        var tail = session.Segments[0];
        var segments = PrepaidPlanner.ForAmountFromSegments(session, start, tail, budget, CafeFixture.Card());
        Assert.Equal(legacy.GrantedActiveSeconds, segments.GrantedActiveSeconds);
        Assert.Equal(legacy.End, segments.End);
        Assert.Equal(legacy.ConsumedToman, segments.ConsumedToman);
    }
}