using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class InvariantAndReferenceTests
{
    [Fact]
    public void Game_timer_rows_for_ps5_3_and_ps5_6_reproduce_the_exported_charges()
    {
        // صفحه اصلی row 5: ps5 (3), 2 نفره, 17:46–21:22, 3:36, 450,000.
        var twoPlayer = CafeFixture.Price(CafeFixture.Session(
            "row5",
            CafeFixture.Play("a", T.At(2026, 6, 2, 17, 46), T.At(2026, 6, 2, 21, 22), "ps5-3", CafeFixture.Ps5, 2)));
        Assert.Equal(216, twoPlayer.TotalBilledMinutes);
        Assert.Equal(450_000, twoPlayer.TotalAmountToman);

        // صفحه اصلی row 6: ps5 (6), 3 نفره, 19:10–20:55, 1:45, 280,000.
        var threePlayer = CafeFixture.Price(CafeFixture.Session(
            "row6",
            CafeFixture.Play("a", T.At(2026, 6, 2, 19, 10), T.At(2026, 6, 2, 20, 55), "ps5-6", CafeFixture.Ps5, 3)));
        Assert.Equal(105, threePlayer.TotalBilledMinutes);
        Assert.Equal(280_000, threePlayer.TotalAmountToman);

        Assert.Equal(5, GameTimerReference.ExampleLevelSeed.Levels[0].DiscountPercent);
        Assert.Equal("Bronze", GameTimerReference.ExampleLevelSeed.Levels[0].Name);
        Assert.Equal(28, GameTimerReference.ExampleLevelSeed.Levels[GameTimerReference.ExampleLevelSeed.Levels.Count - 1].DiscountPercent);
        Assert.Equal("Titan", GameTimerReference.ExampleLevelSeed.Levels[GameTimerReference.ExampleLevelSeed.Levels.Count - 1].Name);
    }

    [Fact]
    public void Whole_minute_same_rate_split_does_not_change_the_total()
    {
        var whole = CafeFixture.Price(CafeFixture.Session(
            "whole",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 12, 0), "ps5-1", CafeFixture.Ps5, 1)));
        var split = CafeFixture.Price(CafeFixture.Session(
            "split",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 17), "ps5-1", CafeFixture.Ps5, 1),
            CafeFixture.Play("b", T.At(2026, 6, 2, 11, 17), T.At(2026, 6, 2, 12, 0), "ps5-1", CafeFixture.Ps5, 1)));

        Assert.Equal(whole.TotalActiveSeconds, split.TotalActiveSeconds);
        Assert.Equal(whole.TotalAmountToman, split.TotalAmountToman);
        Assert.Equal(220_000, whole.TotalAmountToman);
    }

    [Fact]
    public void Same_rate_splits_match_the_unsplit_session()
    {
        var policy = BillingPolicy.Default;
        var rng = new Random(20261103);
        for (var i = 0; i < 40; i++)
        {
            var start = TehranTime.AddSeconds(T.At(2026, 6, 2, 10, 0), rng.Next(0, 8 * 3600));
            var length = rng.Next(1, 3 * 3600);
            var splitAt = rng.Next(0, length + 1);
            var end = TehranTime.AddSeconds(start, length);
            var mid = TehranTime.AddSeconds(start, splitAt);
            var whole = CafeFixture.Price(CafeFixture.Session(
                "whole",
                CafeFixture.Play("a", start, end, "ps5-1", CafeFixture.Ps5, 1)), policy);
            var split = CafeFixture.Price(CafeFixture.Session(
                "split",
                CafeFixture.Play("a", start, mid, "ps5-1", CafeFixture.Ps5, 1),
                CafeFixture.Play("b", mid, end, "ps5-1", CafeFixture.Ps5, 1)), policy);

            Assert.Equal(whole.TotalActiveSeconds, split.TotalActiveSeconds);
            Assert.Equal(whole.TotalBilledMinutes, split.TotalBilledMinutes);
            Assert.Equal(whole.TotalAmountToman, split.TotalAmountToman);
            Assert.Equal(length, whole.TotalActiveSeconds);
        }

        var partialWhole = CafeFixture.Price(CafeFixture.Session(
            "p",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 10, 0, 30), "ps5-1", CafeFixture.Ps5, 1)));
        var partialSplit = CafeFixture.Price(CafeFixture.Session(
            "p2",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 10, 0, 15), "ps5-1", CafeFixture.Ps5, 1),
            CafeFixture.Play("b", T.At(2026, 6, 2, 10, 0, 15), T.At(2026, 6, 2, 10, 0, 30), "ps5-1", CafeFixture.Ps5, 1)));
        Assert.Equal(1, partialWhole.TotalBilledMinutes);
        Assert.Equal(1_833, partialWhole.TotalAmountToman);
        Assert.Equal(1, partialSplit.TotalBilledMinutes);
        Assert.Equal(1_833, partialSplit.TotalAmountToman);
    }

    [Fact]
    public void Seeded_sessions_keep_the_money_and_minute_invariants()
    {
        var rng = new Random(20261103);
        var policy = BillingPolicy.Default;
        for (var i = 0; i < 80; i++)
        {
            var session = RandomSession(rng, i);
            var charge = CafeFixture.Price(session, policy);
            Assert.True(charge.TotalAmountToman >= 0);
            Assert.Equal(ExpectedActiveSeconds(session), charge.TotalActiveSeconds);
            Assert.Equal(charge.Lines.Sum(line => line.ActiveSeconds), charge.TotalActiveSeconds);
            Assert.Equal(charge.Lines.Sum(line => line.BilledMinutes), charge.TotalBilledMinutes);
            Assert.Equal(charge.Lines.Sum(line => line.AmountToman), charge.TotalAmountToman);
            foreach (var line in charge.Lines)
            {
                Assert.True(line.AmountToman >= 0);
                Assert.Equal(TehranTime.WholeSeconds(line.Start, line.End), line.ActiveSeconds);
            }

            var invoice = InvoiceCalculator.Calculate(new InvoiceRequest(
                TimeChargeLine.FromSession(charge),
                rng.Next(2) == 0 ? [] : [new BuffetLine("A_111", "شکلات", 1, 98_000)],
                rng.Next(2) == 0 ? [] : [new ServiceLine("fine", "fine", 10_000)],
                new CustomerPricing(
                    "Bronze",
                    rng.Next(0, 30),
                    JalaliDate.FromGregorian(rng.Next(2) == 0 ? new LocalDate(1990, 6, 2) : new LocalDate(1990, 1, 1)),
                    rng.Next(0, 20)),
                null,
                DiscountPolicy.Default,
                rng.Next(2) == 0
                    ? RoundingPolicy.Off
                    : RoundingPolicy.Create(rng.Next(2) == 0 ? 1_000 : 5_000, (RoundingDirection)rng.Next(0, 3)),
                T.At(2026, 6, 2, 21, 0)));

            Assert.True(invoice.PayableToman >= 0);
            Assert.True(invoice.DiscountToman >= 0);
            Assert.Equal(invoice.GrossToman - invoice.DiscountToman, invoice.NetBeforeRoundingToman);
            Assert.Equal(invoice.NetBeforeRoundingToman + invoice.RoundingToman, invoice.PayableToman);
            Assert.Equal(invoice.Lines.Sum(line => line.AmountToman), invoice.PayableToman);

            if (charge.TotalAmountToman > 0 && invoice.TimeGrossToman > 0)
            {
                var opening = rng.Next(0, 500_000);
                var settled = AccountSettlement.Settle(charge, new PcAccount("m", opening, [new TimePackage("p", rng.Next(0, 90), 200_000)]));
                Assert.Equal(charge.TotalAmountToman, settled.PackageCoveredToman + settled.MoneyConsumedToman + settled.DiscountToman + (settled.Grace?.WaivedToman ?? 0) + settled.UncoveredToman);
                Assert.True(settled.UpdatedAccount.MoneyBalanceToman >= 0);
                Assert.True(settled.MoneyConsumedToman <= opening);
                Assert.Equal(opening - settled.MoneyConsumedToman, settled.UpdatedAccount.MoneyBalanceToman);
                Assert.True(settled.PackageMinutesConsumed >= 0);
            }
        }
    }

    private static Session RandomSession(Random rng, int index)
    {
        var cursor = T.At(2026, 6, 2, rng.Next(0, 24), rng.Next(0, 60));
        var count = rng.Next(1, 4);
        var segments = new List<PlaySegment>();
        for (var i = 0; i < count; i++)
        {
            cursor = TehranTime.AddSeconds(cursor, rng.Next(0, 20) * 60L);
            var length = rng.Next(0, 150) * 60L + rng.Next(0, 30);
            var end = TehranTime.AddSeconds(cursor, length);
            var players = rng.Next(1, 5);
            var group = rng.Next(2) == 0 ? CafeFixture.Ps5 : CafeFixture.ExampleVip;
            var station = group == CafeFixture.Ps5 ? "ps5-1" : "example-vip-1";
            segments.Add(CafeFixture.Play($"s{i}", cursor, end, station, group, players));
            cursor = end;
        }

        IReadOnlyList<PauseInterval>? pauses = null;
        if (rng.Next(2) == 0 && segments.Count > 0)
        {
            var host = segments[0];
            var span = TehranTime.WholeSeconds(host.Start, host.End);
            if (span > 120)
            {
                var pauseStart = TehranTime.AddSeconds(host.Start, span / 4);
                var pauseEnd = TehranTime.AddSeconds(pauseStart, Math.Min(60, span / 5));
                pauses = [new PauseInterval(pauseStart, pauseEnd)];
            }
        }

        return new Session($"rand-{index}", segments, pauses);
    }

    private static long ExpectedActiveSeconds(Session session)
    {
        long total = 0;
        foreach (var segment in session.Segments)
        {
            var pieces = new List<(DateTimeOffset Start, DateTimeOffset End)> { (segment.Start, segment.End) };
            foreach (var pause in session.Pauses ?? [])
            {
                var next = new List<(DateTimeOffset Start, DateTimeOffset End)>();
                foreach (var piece in pieces)
                {
                    if (pause.End <= piece.Start || pause.Start >= piece.End)
                    {
                        next.Add(piece);
                        continue;
                    }

                    if (pause.Start > piece.Start)
                    {
                        next.Add((piece.Start, pause.Start));
                    }

                    if (pause.End < piece.End)
                    {
                        next.Add((pause.End, piece.End));
                    }
                }

                pieces = next;
            }

            foreach (var piece in pieces)
            {
                total += TehranTime.WholeSeconds(piece.Start, piece.End);
            }
        }

        return total;
    }
}
