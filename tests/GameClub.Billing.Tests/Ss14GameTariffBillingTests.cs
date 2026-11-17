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
    public void FixR1_F8_M2_same_rate_different_game_ids_stay_separate_lines_with_game_change_split()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var hop = T.At(2026, 8, 8, 14, 10);
        var end = T.At(2026, 8, 8, 14, 20);
        var charge = Price(
            Ss14Fc27Rates.WithGame(CafeFixture.Play("a", start, hop, "ps5-1", CafeFixture.Ps5, 2)),
            CafeFixture.Play("b", hop, end, "ps5-1", CafeFixture.Ps5, 2) with
            {
                GameTitleId = 2,
                GameDisplayName = "PES",
                GameHourlyRateToman = 170_000,
                GameNightHourlyRateToman = 200_000,
            });
        Assert.Equal(2, charge.Lines.Count);
        Assert.NotEqual(charge.Lines[0].GameTitleId, charge.Lines[1].GameTitleId);
        Assert.Equal(170_000, charge.Lines[0].HourlyRateToman);
        Assert.Equal(170_000, charge.Lines[1].HourlyRateToman);
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
    public void Ss14Example02_OnlineFortyFiveMinutes_145000()
    {
        var start = T.At(2026, 8, 8, 20, 0);
        var end = T.At(2026, 8, 8, 20, 45);
        var charge = CafeFixture.Price(CafeFixture.Session(
            Guid.NewGuid().ToString(),
            Ss14Fc27Rates.WithGame(CafeFixture.Play("a", start, end, "ps5-1", CafeFixture.Ps5, 1, PlayMode.Online))));
        Assert.Equal(142_500, charge.TotalAmountToman);
        var rounded = MoneyMath.Round(charge.TotalAmountToman, RoundingPolicy.Create(5_000, RoundingDirection.Up));
        Assert.Equal(145_000, rounded.Rounded);
    }

    [Fact]
    public void Ss14Example04b_SpecialHoursTwoPlayers_200000()
    {
        var start = T.At(2026, 8, 8, 1, 0);
        var end = T.At(2026, 8, 8, 2, 0);
        var charge = Price(Ss14Fc27Rates.WithGame(CafeFixture.Play("a", start, end, "ps5-1", CafeFixture.Ps5, 2)));
        Assert.Equal(200_000, charge.TotalAmountToman);
    }

    [Fact]
    public void Ss14Example04c_Border0800To0900_TwoLines_165000()
    {
        var start = T.At(2026, 8, 8, 8, 0);
        var end = T.At(2026, 8, 8, 9, 0);
        var charge = Price(Ss14Fc27Rates.WithGame(CafeFixture.Play("a", start, end, "ps5-1", CafeFixture.Ps5, 1)));
        Assert.Equal(165_000, charge.TotalAmountToman);
        Assert.Equal(2, charge.Lines.Count);
    }

    [Fact]
    public void Ss14Example05a_DoubleFractional_125167()
    {
        var start = T.At(2026, 8, 8, 14, 0, 0);
        var hop1 = T.At(2026, 8, 8, 14, 20, 30);
        var hop2 = T.At(2026, 8, 8, 14, 40, 45);
        var end = T.At(2026, 8, 8, 15, 0, 20);
        var charge = Price(
            CafeFixture.Play("a", start, hop1, "ps5-1", CafeFixture.Ps5, 1),
            Ss14Fc27Rates.WithGame(CafeFixture.Play("b", hop1, hop2, "ps5-1", CafeFixture.Ps5, 1) with { SplitReason = "تغییر بازی" }),
            CafeFixture.Play("c", hop2, end, "ps5-1", CafeFixture.Ps5, 1) with { SplitReason = "تغییر بازی" });
        Assert.Equal(125_167, charge.TotalAmountToman);
        Assert.Equal(61, charge.TotalBilledMinutes);
    }

    [Fact]
    public void Ss14Example08_ContinuationAfterRePick_145000()
    {
        var s1 = T.At(2026, 8, 8, 14, 0);
        var tr = T.At(2026, 8, 8, 14, 30);
        var pick = T.At(2026, 8, 8, 14, 40);
        var end = T.At(2026, 8, 8, 15, 0);
        var charge = Price(
            Ss14Fc27Rates.WithGame(CafeFixture.Play("a", s1, tr, "ps5-1", CafeFixture.Ps5, 1)),
            CafeFixture.Play("b", tr, pick, "ps5-2", CafeFixture.Ps5, 1) with { SplitReason = "انتقال" },
            Ss14Fc27Rates.WithGame(CafeFixture.Play("c", pick, end, "ps5-2", CafeFixture.Ps5, 1) with { SplitReason = "تغییر بازی" }));
        Assert.Equal(143_333, charge.TotalAmountToman);
        var rounded = MoneyMath.Round(charge.TotalAmountToman, RoundingPolicy.Create(5_000, RoundingDirection.Up));
        Assert.Equal(145_000, rounded.Rounded);
    }

    [Fact]
    public void Ss14B1_CustomBandWindow_MovesGameNightRate()
    {
        var card = new RateCardBuilder()
            .AddBand(new TimeBand("custom-special", CafeFixture.Ps5, "custom 01:00–07:00", new TimeOfDay(1, 0), new TimeOfDay(7, 0)))
            .AddBase(CafeFixture.Ps5, PlayMode.Offline, 1, 110_000)
            .AddBandRate("custom-special", CafeFixture.Ps5, PlayMode.Offline, 1, 160_000)
            .Build();
        var start = T.At(2026, 8, 8, 2, 0);
        var end = T.At(2026, 8, 8, 3, 0);
        var seg = Ss14Fc27Rates.WithGame(CafeFixture.Play("a", start, end, "ps5-1", CafeFixture.Ps5, 1));
        var charge = SessionPricer.Price(CafeFixture.Session("b", seg), card);
        Assert.Equal(180_000, charge.TotalAmountToman);
        Assert.Equal(RateSource.Game, charge.Lines[0].Source);
    }

    [Fact]
    public void Ss14B6_ThirtyGameChanges_SegmentKeysUnique()
    {
        var start = T.At(2026, 8, 8, 14, 0);
        var segments = new List<PlaySegment>();
        var cursor = start;
        for (var i = 0; i < 30; i++)
        {
            var next = cursor.AddMinutes(1);
            segments.Add(Ss14Fc27Rates.WithGame(CafeFixture.Play($"seg{i}", cursor, next, "ps5-1", CafeFixture.Ps5, 1) with
            {
                SplitReason = "تغییر بازی",
                GameDisplayName = i % 2 == 0 ? "FC27" : "PES",
            }));
            cursor = next;
        }

        var keys = segments.Select(s => s.Id).ToList();
        Assert.Equal(30, keys.Distinct().Count());
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