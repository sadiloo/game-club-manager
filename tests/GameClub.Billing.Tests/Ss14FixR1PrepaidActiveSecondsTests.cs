using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class Ss14FixR1PrepaidActiveSecondsTests
{
    private static Session ConsoleSessionWithPause(
        DateTimeOffset start,
        DateTimeOffset now,
        PauseInterval pause)
    {
        var seg = CafeFixture.Play("a", start, now, "ps5-1", CafeFixture.Ps5, 1);
        return new Session("s", [seg], [pause]);
    }

    [Fact]
    public void FixR1_F8_game_change_after_pause_grants_2760_active_seconds()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var pauseStart = T.At(2026, 8, 8, 14, 10);
        var pauseEnd = T.At(2026, 8, 8, 14, 20);
        var now = T.At(2026, 8, 8, 14, 30);
        var session = ConsoleSessionWithPause(start, now, new PauseInterval(pauseStart, pauseEnd));
        var tail = Ss14Fc27Rates.WithGame(CafeFixture.Play("b", now, now, "ps5-1", CafeFixture.Ps5, 1));
        var plan = PrepaidPlanner.ForAmountFromSegments(session, now, tail, 100_000, CafeFixture.Card());
        Assert.Equal(2_760, plan.GrantedActiveSeconds);
        Assert.Equal(46, plan.GrantedActiveSeconds / 60);
    }

    [Fact]
    public void FixR1_F8_player_count_change_after_pause_grants_3000_active_seconds()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var pause = new PauseInterval(T.At(2026, 8, 8, 14, 5), T.At(2026, 8, 8, 14, 15));
        var now = T.At(2026, 8, 8, 14, 20);
        var session = new Session(
            "s",
            [
                CafeFixture.Play("a", start, now, "ps5-1", CafeFixture.Ps5, 1),
            ],
            [pause]);
        var tail = CafeFixture.Play("b", now, now, "ps5-1", CafeFixture.Ps5, 2) with { SplitReason = "تعداد بازیکن" };
        var plan = PrepaidPlanner.ForAmountFromSegments(session, now, tail, 100_000, CafeFixture.Card());
        Assert.Equal(3_000, plan.GrantedActiveSeconds);
    }

    [Fact]
    public void FixR1_F8_transfer_after_pause_grants_2880_active_seconds()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var pause = new PauseInterval(T.At(2026, 8, 8, 14, 10), T.At(2026, 8, 8, 14, 20));
        var now = T.At(2026, 8, 8, 14, 30);
        var session = new Session(
            "s",
            [
                Ss14Fc27Rates.WithGame(CafeFixture.Play("a", start, now, "ps5-1", CafeFixture.Ps5, 1)),
            ],
            [pause]);
        var tail = CafeFixture.Play("b", now, now, "ps5-1", CafeFixture.Ps5, 1) with { SplitReason = "انتقال" };
        var plan = PrepaidPlanner.ForAmountFromSegments(session, now, tail, 100_000, CafeFixture.Card());
        Assert.Equal(2_880, plan.GrantedActiveSeconds);
    }

    [Fact]
    public void FixR1_F8_D6_mid_session_prepaid_after_pause_grants_2760_active_seconds()
    {
        var start = T.At(2026, 8, 8, 7, 50);
        var pause = new PauseInterval(T.At(2026, 8, 8, 8, 0), T.At(2026, 8, 8, 8, 20));
        var now = T.At(2026, 8, 8, 8, 25);
        var session = ConsoleSessionWithPause(start, now, pause);
        var tail = session.Segments[0];
        var plan = PrepaidPlanner.ForAmountFromSegments(session, now, tail, 100_000, CafeFixture.Card());
        Assert.Equal(2_760, plan.GrantedActiveSeconds);
    }

}