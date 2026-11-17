using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class Ss14GameTariffBillingTests
{
  private static SessionCharge Price(params PlaySegment[] segments)
        => CafeFixture.Price(CafeFixture.Session(Guid.NewGuid().ToString(), segments));

    [Fact]
    public void Ss14Example01_MidSessionGameChange_130000()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var hop = T.At(2026, 8, 8, 14, 30);
        var end = T.At(2026, 8, 8, 15, 0);
        var charge = Price(
            CafeFixture.Play("a", start, hop, "ps5-1", CafeFixture.Ps5, 1),
            Ss14Fc27Rates.WithGame(CafeFixture.Play("b", hop, end, "ps5-1", CafeFixture.Ps5, 1) with { SplitReason = "تغییر بازی" }));
        Assert.Equal(130_000, charge.TotalAmountToman);
        Assert.Equal(2, charge.Lines.Count);
    }

    [Fact]
    public void Ss14Example06_RemoveGame_125000()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var hop = T.At(2026, 8, 8, 14, 20);
        var end = T.At(2026, 8, 8, 15, 0);
        var charge = Price(
            Ss14Fc27Rates.WithGame(CafeFixture.Play("a", start, hop, "ps5-1", CafeFixture.Ps5, 1) with { SplitReason = "تغییر بازی" }),
            CafeFixture.Play("b", hop, end, "ps5-1", CafeFixture.Ps5, 1) with { SplitReason = "تغییر بازی" });
        Assert.Equal(123_333, charge.TotalAmountToman);
    }

    [Fact]
    public void Ss14Example07_TwoGamesSameRate_56666()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var hop = T.At(2026, 8, 8, 14, 10);
        var end = T.At(2026, 8, 8, 14, 20);
        var seg1 = Ss14Fc27Rates.WithGame(CafeFixture.Play("a", start, hop, "ps5-1", CafeFixture.Ps5, 2) with { SplitReason = "تغییر بازی", GameDisplayName = "FC27" });
        var seg2 = Ss14Fc27Rates.WithGame(CafeFixture.Play("b", hop, end, "ps5-1", CafeFixture.Ps5, 2) with
        {
            SplitReason = "تغییر بازی",
            GameDisplayName = "PES",
        });
        var charge = Price(seg1, seg2);
        Assert.Equal(56_666, charge.TotalAmountToman);
        Assert.Equal(2, charge.Lines.Count);
    }

    [Fact]
    public void Ss14Example03_FractionalHop_129333()
    {
        var start = T.At(2026, 8, 8, 14, 0, 0);
        var hop = T.At(2026, 8, 8, 14, 30, 30);
        var end = T.At(2026, 8, 8, 15, 0, 0);
        var charge = Price(
            CafeFixture.Play("a", start, hop, "ps5-1", CafeFixture.Ps5, 1),
            Ss14Fc27Rates.WithGame(CafeFixture.Play("b", hop, end, "ps5-1", CafeFixture.Ps5, 1) with { SplitReason = "تغییر بازی" }));
        Assert.Equal(129_333, charge.TotalAmountToman);
        Assert.Equal(60, charge.TotalBilledMinutes);
    }

    [Fact]
    public void Ss14Example12_SubMinuteGame_NoZeroLine_56667()
    {
        var start = T.At(2026, 8, 8, 14, 0, 0);
        var hop1 = T.At(2026, 8, 8, 14, 10, 10);
        var hop2 = T.At(2026, 8, 8, 14, 10, 30);
        var end = T.At(2026, 8, 8, 14, 20, 0);
        var charge = Price(
            Ss14Fc27Rates.WithGame(CafeFixture.Play("a", start, hop1, "ps5-1", CafeFixture.Ps5, 2) with { SplitReason = "تغییر بازی", GameDisplayName = "FC27" }),
            Ss14Fc27Rates.WithGame(CafeFixture.Play("b", hop1, hop2, "ps5-1", CafeFixture.Ps5, 2) with { SplitReason = "تغییر بازی", GameDisplayName = "PES" }),
            Ss14Fc27Rates.WithGame(CafeFixture.Play("c", hop2, end, "ps5-1", CafeFixture.Ps5, 2) with { SplitReason = "تغییر بازی", GameDisplayName = "FC27" }));
        Assert.Equal(56_667, charge.TotalAmountToman);
        Assert.Equal(2, charge.Lines.Count);
        Assert.All(charge.Lines, l => Assert.Contains("FC27", l.GameDisplayName ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void Ss14Example02_PlayerCountFourToTwo_260000()
    {
        var start = T.At(2026, 8, 8, 20, 0);
        var hop = T.At(2026, 8, 8, 20, 40);
        var end = T.At(2026, 8, 8, 21, 10);
        var charge = Price(
            Ss14Fc27Rates.WithGame(CafeFixture.Play("a", start, hop, "ps5-2", CafeFixture.Ps5, 4) with { SplitReason = "تغییر بازی" }),
            Ss14Fc27Rates.WithGame(CafeFixture.Play("b", hop, end, "ps5-2", CafeFixture.Ps5, 2) with { SplitReason = "تغییر بازی" }));
        Assert.Equal(258_333, charge.TotalAmountToman);
    }

    [Fact]
    public void Ss14Example04a_SpecialHoursGame_180000()
    {
        var start = T.At(2026, 8, 8, 1, 0);
        var end = T.At(2026, 8, 8, 2, 0);
        var charge = Price(Ss14Fc27Rates.WithGame(CafeFixture.Play("a", start, end, "ps5-1", CafeFixture.Ps5, 1)));
        Assert.Equal(180_000, charge.TotalAmountToman);
        Assert.Equal(RateSource.Game, charge.Lines[0].Source);
    }

    [Fact]
    public void Ss14Example04d_EmptyNightUsesNormal_150000()
    {
        var start = T.At(2026, 8, 8, 8, 0);
        var end = T.At(2026, 8, 8, 9, 0);
        var seg = CafeFixture.Play("a", start, end, "ps5-1", CafeFixture.Ps5, 1);
        var charge = Price(Ss14Fc27Rates.WithGameNoNight(seg));
        Assert.Equal(150_000, charge.TotalAmountToman);
        Assert.Single(charge.Lines);
    }

    [Fact]
    public void Ss14Example05b_MirrorFixR2_6166()
    {
        var card = CafeFixture.Card();
        var policy = BillingPolicy.Default;
        var start = T.At(2026, 8, 8, 14, 0);
        var hop1 = start.AddSeconds(30.75);
        var hop2 = start.AddSeconds(60.75);
        var end = start.AddSeconds(121.5);
        var station = GameTimerReference.Ps5OnePlayerHourly;
        var unsplit = SessionPricer.Price(
            CafeFixture.Session("u", CafeFixture.Play("a", start, end, "ps5-1", CafeFixture.Ps5, 1)),
            card,
            policy: policy);
        var split = SessionPricer.Price(
            CafeFixture.Session(
                "s",
                CafeFixture.Play("a", start, hop1, "ps5-1", CafeFixture.Ps5, 1),
                Ss14Fc27Rates.WithGame(CafeFixture.Play("b", hop1, hop2, "ps5-1", CafeFixture.Ps5, 1) with { SplitReason = "تغییر بازی" }),
                CafeFixture.Play("c", hop2, end, "ps5-1", CafeFixture.Ps5, 1) with { SplitReason = "تغییر بازی" }),
            card,
            policy: policy);
        Assert.Equal(unsplit.TotalBilledMinutes, split.TotalBilledMinutes);
        Assert.Equal(6_166, split.TotalAmountToman);
        _ = station;
    }

    [Fact]
    public void Ss14E4_DiscountCode_ExcludesGameLinesWhenFalse()
    {
        var lines = new[]
        {
            new TimeChargeLine("station", 50_000, CafeFixture.Ps5, TehranTime.Date(T.At(2026, 1, 1, 12, 0)), DayOfWeek.Monday, IsGameLine: false),
            new TimeChargeLine("FC27", 75_000, CafeFixture.Ps5, TehranTime.Date(T.At(2026, 1, 1, 12, 0)), DayOfWeek.Monday, IsGameLine: true, GameTitleId: 1),
        };
        var code = new DiscountCode("x", 10, 0, null, null, new[] { CafeFixture.Ps5 }, null, ChargeScope.Time, AppliesToGameLines: false);
        var inv = InvoiceCalculator.Calculate(new InvoiceRequest(
            lines,
            [],
            [],
            null,
            code,
            DiscountPolicy.Default,
            RoundingPolicy.Off,
            T.At(2026, 1, 1, 12, 0)));
        Assert.Equal(5_000, inv.DiscountToman);
    }
}