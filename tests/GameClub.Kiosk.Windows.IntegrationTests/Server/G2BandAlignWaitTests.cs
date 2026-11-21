#if NET10_0_OR_GREATER
using GameClub.Billing;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

public sealed class G2BandAlignWaitTests
{
    private static readonly TimeBand Band = PilotSeed.PcNormalSpecialHours;

    private static IEnumerable<TimeOfDay> Edges() => G2BandAlignWait.BandEdges(Band);

    [Fact]
    public void approach_window_0830_inclusive_at_exactly_thirteen_minutes()
    {
        var now = TehranTime.At(2026, 6, 3, 8, 17, 0);
        var plan = G2BandAlignWait.ComputePlan(now.ToUniversalTime(), Edges());
        Assert.True(plan.WaitMilliseconds > 0, "now == edge − 13min waits (inclusive)");
    }

    [Fact]
    public void approach_window_0830_exclusive_one_second_before_thirteen_minutes()
    {
        var now = TehranTime.At(2026, 6, 3, 8, 16, 59);
        var plan = G2BandAlignWait.ComputePlan(now.ToUniversalTime(), Edges());
        Assert.Equal(0, plan.WaitMilliseconds);
    }

    [Fact]
    public void approach_window_midnight_inclusive_at_exactly_thirteen_minutes()
    {
        var now = TehranTime.At(2026, 6, 3, 23, 47, 0);
        var plan = G2BandAlignWait.ComputePlan(now.ToUniversalTime(), Edges());
        Assert.True(plan.WaitMilliseconds > 0);
    }

    [Fact]
    public void waits_one_second_before_0830_edge()
    {
        var now = TehranTime.At(2026, 6, 3, 8, 29, 59);
        var plan = G2BandAlignWait.ComputePlan(now.ToUniversalTime(), Edges());
        Assert.True(plan.WaitMilliseconds >= 6000, "wait through edge plus 5s buffer");
    }

    [Fact]
    public void waits_when_exactly_on_0830_edge()
    {
        var now = TehranTime.At(2026, 6, 3, 8, 30, 0);
        var plan = G2BandAlignWait.ComputePlan(now.ToUniversalTime(), Edges());
        Assert.InRange(plan.WaitMilliseconds, 1, 6000);
    }

    [Fact]
    public void no_wait_one_second_after_0830_edge()
    {
        var now = TehranTime.At(2026, 6, 3, 8, 30, 1);
        var plan = G2BandAlignWait.ComputePlan(now.ToUniversalTime(), Edges());
        Assert.Equal(0, plan.WaitMilliseconds);
    }

    [Fact]
    public void waits_one_second_before_midnight_edge()
    {
        var now = TehranTime.At(2026, 6, 3, 23, 59, 59);
        var plan = G2BandAlignWait.ComputePlan(now.ToUniversalTime(), Edges());
        Assert.True(plan.WaitMilliseconds >= 6000);
    }

    [Fact]
    public void waits_when_exactly_on_midnight_edge()
    {
        var now = TehranTime.At(2026, 6, 4, 0, 0, 0);
        var plan = G2BandAlignWait.ComputePlan(now.ToUniversalTime(), Edges());
        Assert.Equal(5000, plan.WaitMilliseconds);
    }

    [Fact]
    public void no_wait_one_second_after_midnight_edge()
    {
        var now = TehranTime.At(2026, 6, 4, 0, 0, 1);
        var plan = G2BandAlignWait.ComputePlan(now.ToUniversalTime(), Edges());
        Assert.Equal(0, plan.WaitMilliseconds);
    }

    [Fact]
    public void approach_window_midnight_at_thirteen_minutes_plus_one_second_waits()
    {
        var now = TehranTime.At(2026, 6, 3, 23, 47, 1);
        var plan = G2BandAlignWait.ComputePlan(now.ToUniversalTime(), Edges());
        Assert.True(plan.WaitMilliseconds > 0);
    }

    [Fact]
    public void no_wait_far_from_edges_noon()
    {
        var now = TehranTime.At(2026, 6, 3, 12, 0, 0);
        var plan = G2BandAlignWait.ComputePlan(now.ToUniversalTime(), Edges());
        Assert.Equal(0, plan.WaitMilliseconds);
    }

    [Fact]
    public void midnight_wrap_approach_uses_next_calendar_midnight()
    {
        var now = TehranTime.At(2026, 6, 3, 23, 50, 0);
        var plan = G2BandAlignWait.ComputePlan(now.ToUniversalTime(), Edges());
        Assert.True(plan.WaitMilliseconds > 0);
        Assert.Equal(0, plan.TriggeringEdgeTehran!.Value.Hour);
        Assert.Equal(0, plan.TriggeringEdgeTehran.Value.Minute);
    }

    [Fact]
    public void assert_fails_when_instants_straddle_band_edge()
    {
        var start = TehranTime.At(2026, 6, 3, 8, 29, 0);
        var checkpoint = TehranTime.At(2026, 6, 3, 8, 31, 0);
        var ex = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            G2BandAlignWait.AssertSameSideOfAllBandEdges(
                Band,
                start.ToUniversalTime(),
                new[] { checkpoint.UtcDateTime }));
        Assert.Contains("straddle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void assert_passes_when_all_instants_same_side_of_0830()
    {
        var start = TehranTime.At(2026, 6, 3, 8, 31, 0);
        var checkpoint = TehranTime.At(2026, 6, 3, 8, 40, 0);
        G2BandAlignWait.AssertSameSideOfAllBandEdges(
            Band,
            start.ToUniversalTime(),
            new[] { checkpoint.UtcDateTime });
    }
}
#endif