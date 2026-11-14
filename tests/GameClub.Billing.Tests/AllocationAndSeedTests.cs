using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class AllocationAndSeedTests
{
    private static readonly RoundingPolicy StepThousandUp = RoundingPolicy.Create(1_000, RoundingDirection.Up);
    private static readonly RoundingPolicy StepThousandUpTime = RoundingPolicy.Create(1_000, RoundingDirection.Up, RoundingScope.TimeChargesOnly);

    [Fact]
    public void Pilot_seed_prices_pc_groups_like_consoles_and_rounds_the_counter_by_5000_up()
    {
        var card = PilotSeed.CreateCard();
        Assert.Equal(80_000, Hour(card, PilotSeed.PcNormalGroupId, "pc-1", 10));
        Assert.Equal(100_000, Hour(card, PilotSeed.PcNormalGroupId, "pc-1", 2));
        Assert.Equal(110_000, Hour(card, PilotSeed.PcVipGroupId, "pc-vip-1", 10));
        Assert.Equal(130_000, Hour(card, PilotSeed.PcVipGroupId, "pc-vip-1", 2));

        var festival = new RateCardBuilder()
            .AddBand(PilotSeed.PcNormalSpecialHours)
            .AddBase(PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, PilotSeed.PcNormalHourly)
            .AddBandRate(PilotSeed.PcNormalBandId, PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, PilotSeed.PcNormalSpecialHourly)
            .AddSpecialRate("opening", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, 50_000)
            .Build();
        var days = new SpecialDayCalendar(
            [SpecialDay.Once(JalaliDate.FromGregorian(new LocalDate(2026, 6, 4)), "Opening", "opening")]);
        var onDay = SessionPricer.Price(
            CafeFixture.Session("d", CafeFixture.Play("a", T.At(2026, 6, 4, 2, 0), T.At(2026, 6, 4, 3, 0), "pc-1", PilotSeed.PcNormalGroupId, 1)),
            festival,
            days);
        Assert.Equal(RateSource.SpecialDay, Assert.Single(onDay.Lines).Source);
        Assert.Equal(50_000, onDay.TotalAmountToman);

        var rounding = PilotSeed.CounterRounding;
        Assert.True(rounding.Enabled);
        Assert.Equal(5_000, rounding.StepToman);
        Assert.Equal(RoundingDirection.Up, rounding.Direction);
        Assert.Equal(RoundingScope.WholeInvoice, rounding.Scope);

        var mixed = PaymentAllocator.Allocate(
            [Line(PilotSeed.PcNormalGroupId, true, 80_000), Line(CafeFixture.Ps5, false, 12_000)],
            [
                new Tender(PaymentMethod.Wallet, 80_000, "m"),
                new Tender(PaymentMethod.Cash, 15_000),
            ],
            rounding,
            [new MemberWallet("m", 80_000, 0)]);
        Assert.True(mixed.IsValid);
        Assert.Equal(80_000, mixed.Tenders[0].AmountToman);
        Assert.Equal(3_000, mixed.RoundingToman);
        Assert.Equal(95_000, mixed.PayableToman);
    }

    [Fact]
    public void Started_minutes_are_a_ceiling_and_a_pause_does_not_split_the_line()
    {
        var plain = PlaySeconds(620, 0);
        Assert.Equal(11, plain.TotalBilledMinutes);
        Assert.Single(plain.Lines);

        var exact = PlaySeconds(600, 0);
        Assert.Equal(10, exact.TotalBilledMinutes);

        var paused = PlaySeconds(620, 2);
        Assert.Equal(11, paused.TotalBilledMinutes);
        Assert.Single(paused.Lines);
        Assert.Equal(plain.TotalAmountToman, paused.TotalAmountToman);

        var withPause = CafeFixture.Price(new Session(
            "p",
            [CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 10, 50), "ps5-1", CafeFixture.Ps5, 1)],
            [new PauseInterval(T.At(2026, 6, 2, 10, 20), T.At(2026, 6, 2, 10, 30))]));
        var line = Assert.Single(withPause.Lines);
        Assert.Equal(40, line.BilledMinutes);
        Assert.Equal(73_333, line.AmountToman);

        var unpaused = CafeFixture.Price(CafeFixture.Session(
            "u",
            CafeFixture.Play("a", T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 10, 40), "ps5-1", CafeFixture.Ps5, 1)));
        Assert.Equal(unpaused.TotalAmountToman, withPause.TotalAmountToman);
        Assert.Equal(unpaused.TotalBilledMinutes, withPause.TotalBilledMinutes);
    }

    [Fact]
    public void A_minute_that_starts_before_a_special_day_stays_on_the_starting_rate()
    {
        var card = new RateCardBuilder()
            .AddBase(CafeFixture.Ps5, PlayMode.Offline, 1, 110_000)
            .AddBand(GameTimerReference.SpecialHours)
            .AddBandRate(GameTimerReference.SpecialBandId, CafeFixture.Ps5, PlayMode.Offline, 1, 160_000)
            .AddSpecialRate("festival", CafeFixture.Ps5, PlayMode.Offline, 1, 200_000)
            .Build();
        var days = new SpecialDayCalendar(
            [SpecialDay.Once(JalaliDate.FromGregorian(new LocalDate(2026, 6, 3)), "Festival", "festival")]);
        var charge = SessionPricer.Price(
            CafeFixture.Session("cross", CafeFixture.Play("a", T.At(2026, 6, 2, 23, 59, 30), T.At(2026, 6, 3, 0, 1, 30), "ps5-1", CafeFixture.Ps5, 1)),
            card,
            days);
        Assert.Equal(2, charge.TotalBilledMinutes);
        Assert.Equal(1_833, charge.Lines[0].AmountToman);
        Assert.Equal(3_333, charge.Lines[1].AmountToman);
        Assert.Equal(5_166, charge.TotalAmountToman);
    }

    [Fact]
    public void Grace_is_a_full_minute_across_midnight_and_is_priced_where_it_starts()
    {
        var night = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 23, 59, 40),
            500,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        Assert.Equal(T.At(2026, 6, 3, 0, 0, 40), night.End);
        Assert.Equal(60, night.GrantedActiveSeconds);
        Assert.Equal(1_833, Assert.Single(night.Lines).AmountToman);
        Assert.Equal(1_833, night.Grace!.WaivedToman);
        Assert.Equal(RateSource.BaseTariff, night.Lines[0].Source);

        var band = PrepaidPlanner.ForAmount(
            T.At(2026, 6, 2, 0, 10),
            500,
            "pre",
            "ps5-1",
            CafeFixture.Ps5,
            PlayMode.Offline,
            1,
            CafeFixture.Card());
        Assert.Equal(2_667, Assert.Single(band.Lines).AmountToman);
        Assert.Equal(2_667, band.Grace!.WaivedToman);
    }

    [Fact]
    public void Rc90_time_only_rounding_uses_the_pro_rata_counter_remainder()
    {
        var result = Pay(
            [Line(CafeFixture.Ps5, false, 123_750), Snack(12_350)],
            [Wallet(50_000), Cash(86_813)],
            StepThousandUpTime);
        Assert.True(result.IsValid, result.Errors.FirstOrDefault()?.Message);
        Assert.Equal([45_463L, 4_537L], Shares(result, 0));
        Assert.Equal(50_000, result.Tenders[0].AmountToman);
        Assert.Equal(713, result.RoundingToman);
        Assert.Equal(136_813, result.PayableToman);
    }

    [Fact]
    public void Largest_remainder_ties_go_to_the_earlier_line()
    {
        var tie = Pay([Line("a", false, 10_000), Line("b", false, 10_000)], [Wallet(1), Cash(19_999)], RoundingPolicy.Off);
        Assert.Equal([1L, 0L], Shares(tie, 0));

        var three = Pay(
            [Line("a", false, 1_000), Line("b", false, 1_000), Line("c", false, 1_000)],
            [Wallet(100), Cash(2_900)],
            RoundingPolicy.Off);
        Assert.Equal([34L, 33L, 33L], Shares(three, 0));

        var twoLeft = Pay(
            [Line("a", false, 1_000), Line("b", false, 1_000), Line("c", false, 1_000)],
            [Wallet(2), Cash(2_998)],
            RoundingPolicy.Off);
        Assert.Equal([1L, 1L, 0L], Shares(twoLeft, 0));

        var unequal = Pay(
            [Line("a", false, 7_000), Line("b", false, 2_000), Line("c", false, 1_000)],
            [Wallet(1_001), Cash(8_999)],
            RoundingPolicy.Off);
        Assert.Equal([701L, 200L, 100L], Shares(unequal, 0));
    }

    [Fact]
    public void Balance_tenders_are_exact_and_rounding_does_not_create_a_payment()
    {
        var full = Pay(Rc81Lines(), [Wallet(136_100)], StepThousandUp, balance: 200_000);
        Assert.True(full.IsValid, full.Errors.FirstOrDefault()?.Message);
        Assert.Equal(136_100, full.Tenders.Single().AmountToman);
        Assert.Equal(0, full.RoundingToman);
        Assert.Equal(63_900, full.RemainingWallets.Single().BalanceToman);
        Assert.Single(full.Tenders);

        var alreadyRound = Pay(Rc81Lines(), [Wallet(36_100), Cash(100_000)], StepThousandUp);
        Assert.True(alreadyRound.IsValid, alreadyRound.Errors.FirstOrDefault()?.Message);
        Assert.Equal(0, alreadyRound.RoundingToman);
        Assert.Equal(100_000, alreadyRound.Tenders[1].AmountToman);

        var nearest = Pay(
            Rc81Lines(),
            [Wallet(50_000), Cash(86_000)],
            RoundingPolicy.Create(1_000, RoundingDirection.Nearest));
        Assert.True(nearest.IsValid, nearest.Errors.FirstOrDefault()?.Message);
        Assert.Equal(50_000, nearest.Tenders[0].AmountToman);
        Assert.Equal(-100, nearest.RoundingToman);
        Assert.Equal(136_000, nearest.PayableToman);

        var invoiceTotal = Pay(Rc81Lines(), [Wallet(50_000), Cash(87_000)], StepThousandUp);
        Assert.True(invoiceTotal.IsValid, invoiceTotal.Errors.FirstOrDefault()?.Message);
        Assert.Equal(2, invoiceTotal.Tenders.Count);
        Assert.Equal(900, invoiceTotal.RoundingToman);
        Assert.Equal(137_000, invoiceTotal.PayableToman);

        var prepaid = Pay([Line(CafeFixture.Ps5, false, 67_833)], [Wallet(67_833)], StepThousandUp, balance: 100_000);
        Assert.Equal(67_833, prepaid.Tenders.Single().AmountToman);
        Assert.Equal(0, prepaid.RoundingToman);
        Assert.Equal(32_167, prepaid.RemainingWallets.Single().BalanceToman);

        var grace = Pay([Line(PilotSeed.PcNormalGroupId, true, 500)], [new Tender(PaymentMethod.PcBalance, 500)], StepThousandUp);
        Assert.Equal(500, grace.BalanceAppliedToman);
        Assert.Equal(0, grace.RoundingToman);
        Assert.Equal(0, grace.CounterExactToman);
    }

    [Fact]
    public void Pc_balance_pays_only_pc_time_then_the_wallet_pays_the_rest()
    {
        var lines = new[]
        {
            Line(PilotSeed.PcNormalGroupId, true, 30_000),
            Line(CafeFixture.Ps5, false, 62_500),
            Snack(7_650),
        };
        var separate = PaymentAllocator.Allocate(
            lines,
            [new Tender(PaymentMethod.PcBalance, 20_000), Cash(81_000)],
            StepThousandUp);
        Assert.True(separate.IsValid, separate.Errors.FirstOrDefault()?.Message);
        Assert.Equal([20_000L, 0L, 0L], Shares(separate, 0));
        Assert.Equal(850, separate.RoundingToman);
        Assert.Equal(101_000, separate.PayableToman);

        var timeOnly = PaymentAllocator.Allocate(
            lines,
            [new Tender(PaymentMethod.PcBalance, 20_000), Cash(80_650)],
            StepThousandUpTime);
        Assert.True(timeOnly.IsValid, timeOnly.Errors.FirstOrDefault()?.Message);
        Assert.Equal(500, timeOnly.RoundingToman);
        Assert.Equal(80_650, timeOnly.CounterExactToman + timeOnly.RoundingToman);
        Assert.Equal(100_650, timeOnly.PayableToman);

        var capped = PaymentAllocator.Allocate(
            lines,
            [new Tender(PaymentMethod.PcBalance, 50_000), Cash(71_000)],
            StepThousandUp);
        Assert.True(capped.IsValid, capped.Errors.FirstOrDefault()?.Message);
        Assert.Equal(30_000, capped.Tenders[0].AmountToman);
        Assert.Equal(850, capped.RoundingToman);

        // 62,500 is not a multiple of 1,000, so the time-line remainder rounds up by 500.
        var timeExact = PaymentAllocator.Allocate(
            lines,
            [new Tender(PaymentMethod.PcBalance, 50_000), Cash(70_650)],
            StepThousandUpTime);
        Assert.True(timeExact.IsValid, timeExact.Errors.FirstOrDefault()?.Message);
        Assert.Equal(500, timeExact.RoundingToman);

        var both = PaymentAllocator.Allocate(
            lines,
            [Wallet(30_000), new Tender(PaymentMethod.PcBalance, 20_000), Cash(51_000)],
            StepThousandUp,
            [new MemberWallet("m", 30_000, 0)]);
        Assert.True(both.IsValid, both.Errors.FirstOrDefault()?.Message);
        Assert.Equal([20_000L, 0L, 0L], Shares(both, 1));
        Assert.Equal([3_743L, 23_394L, 2_863L], Shares(both, 0));
        Assert.Equal(850, both.RoundingToman);

        var bothTime = PaymentAllocator.Allocate(
            lines,
            [new Tender(PaymentMethod.PcBalance, 20_000), Wallet(30_000), Cash(50_787)],
            StepThousandUpTime,
            [new MemberWallet("m", 30_000, 0)]);
        Assert.True(bothTime.IsValid, bothTime.Errors.FirstOrDefault()?.Message);
        Assert.Equal(637, bothTime.RoundingToman);
        Assert.Equal(50_787, bothTime.CounterExactToman + bothTime.RoundingToman);

        var rejected = PaymentAllocator.Allocate(
            [Line(CafeFixture.Ps5, false, 62_500), Snack(7_650)],
            [new Tender(PaymentMethod.PcBalance, 20_000), Cash(71_000)],
            StepThousandUp);
        Assert.False(rejected.IsValid);
        Assert.Contains(rejected.Errors, error => error.Code == PaymentErrors.PcBalanceNoPcLine);
        Assert.Equal(0, rejected.Tenders[0].AmountToman);
        Assert.Empty(rejected.Shares);
    }

    [Fact]
    public void Shared_pocket_is_one_pro_rata_tender_and_grace_is_already_in_the_base()
    {
        var shared = Pay(
            [
                Line(PilotSeed.PcNormalGroupId, true, 30_000),
                Line(CafeFixture.Ps5, false, 62_500),
                Snack(7_650),
            ],
            [Wallet(50_000), Cash(51_000)],
            StepThousandUp);
        Assert.True(shared.IsValid, shared.Errors.FirstOrDefault()?.Message);
        Assert.Equal([14_978L, 31_203L, 3_819L], Shares(shared, 0));
        Assert.Equal(850, shared.RoundingToman);
        Assert.Equal(101_000, shared.PayableToman);

        var sharedTime = Pay(
            [
                Line(PilotSeed.PcNormalGroupId, true, 30_000),
                Line(CafeFixture.Ps5, false, 62_500),
                Snack(7_650),
            ],
            [Wallet(50_000), Cash(50_831)],
            StepThousandUpTime);
        Assert.True(sharedTime.IsValid, sharedTime.Errors.FirstOrDefault()?.Message);
        Assert.Equal(681, sharedTime.RoundingToman);

        var afterGrace = PaymentAllocator.Allocate(
            [
                Line(PilotSeed.PcNormalGroupId, true, 10_500),
                Line(CafeFixture.Ps5, false, 62_500),
                Snack(7_650),
            ],
            [new Tender(PaymentMethod.PcBalance, 10_500), Wallet(30_000), Cash(41_000)],
            StepThousandUp,
            [new MemberWallet("m", 30_000, 0)]);
        Assert.True(afterGrace.IsValid, afterGrace.Errors.FirstOrDefault()?.Message);
        Assert.Equal(10_500, afterGrace.Tenders[0].AmountToman);
        Assert.Equal([0L, 26_728L, 3_272L], Shares(afterGrace, 1));
        Assert.Equal(0, afterGrace.CounterRemainderByLine[0]);
        Assert.Equal(850, afterGrace.RoundingToman);

        var afterGraceTime = PaymentAllocator.Allocate(
            [
                Line(PilotSeed.PcNormalGroupId, true, 10_500),
                Line(CafeFixture.Ps5, false, 62_500),
                Snack(7_650),
            ],
            [new Tender(PaymentMethod.PcBalance, 10_500), Wallet(30_000), Cash(40_378)],
            StepThousandUpTime,
            [new MemberWallet("m", 30_000, 0)]);
        Assert.True(afterGraceTime.IsValid, afterGraceTime.Errors.FirstOrDefault()?.Message);
        Assert.Equal(228, afterGraceTime.RoundingToman);
    }

    [Fact]
    public void Pro_rata_shares_stay_exact_over_a_seeded_mix_of_tenders()
    {
        var rng = new Random(20261103);
        for (var i = 0; i < 200; i++)
        {
            var pc = rng.Next(0, 40_000);
            var ps5 = rng.Next(0, 40_000);
            var snack = rng.Next(0, 20_000);
            var lines = new[]
            {
                Line(PilotSeed.PcNormalGroupId, true, pc),
                Line(CafeFixture.Ps5, false, ps5),
                Snack(snack),
            };
            var separate = rng.Next(2) == 0;
            var pcOffer = separate ? rng.Next(0, pc + 5_000) : 0;
            var coverAfterPc = Math.Max(0, pc - Math.Min(pcOffer, pc)) + ps5 + snack;
            var walletOffer = rng.Next(0, coverAfterPc + 1);
            var first = PaymentAllocator.Allocate(lines, Tenders(separate, pcOffer, walletOffer, 0), RoundingPolicy.Off, [new MemberWallet("m", 1_000_000, 0)]);
            var second = PaymentAllocator.Allocate(lines, Tenders(separate, pcOffer, walletOffer, 0), RoundingPolicy.Off, [new MemberWallet("m", 1_000_000, 0)]);
            Assert.Equal(first.Shares.Select(share => share.AmountToman), second.Shares.Select(share => share.AmountToman));
            AssertShares(lines, first, separate);
        }
    }

    private static void AssertShares(PayableLine[] lines, AllocationResult result, bool separate)
    {
        var covered = new long[lines.Length];
        foreach (var share in result.Shares)
        {
            Assert.True(share.AmountToman >= 0);
            Assert.True(share.AmountToman <= lines[share.LineIndex].NetToman);
            covered[share.LineIndex] += share.AmountToman;
            if (result.Tenders[share.TenderIndex].Method == PaymentMethod.PcBalance)
            {
                Assert.True(lines[share.LineIndex].IsPcTime);
            }
        }

        foreach (var tender in result.Tenders.Where(tender => tender.Accepted && tender.Method is PaymentMethod.PcBalance or PaymentMethod.Wallet))
        {
            var sum = result.Shares.Where(share => share.TenderIndex == tender.Index).Sum(share => share.AmountToman);
            Assert.Equal(tender.AmountToman, sum);
        }

        if (separate)
        {
            var pcIndex = -1;
            var walletIndex = -1;
            for (var n = 0; n < result.Tenders.Count; n++)
            {
                if (result.Tenders[n].Method == PaymentMethod.PcBalance)
                {
                    pcIndex = n;
                }

                if (result.Tenders[n].Method == PaymentMethod.Wallet)
                {
                    walletIndex = n;
                }
            }

            if (pcIndex >= 0 && walletIndex >= 0)
            {
                Assert.True(pcIndex < walletIndex);
            }
        }
        else
        {
            Assert.Equal(1, result.Tenders.Count(tender => tender.Method == PaymentMethod.Wallet));
        }

        for (var i = 0; i < lines.Length; i++)
        {
            Assert.True(lines[i].NetToman - covered[i] >= 0);
        }
    }

    private static List<Tender> Tenders(bool separate, int pcOffer, int walletOffer, int cash)
    {
        var tenders = new List<Tender>();
        if (separate)
        {
            tenders.Add(new Tender(PaymentMethod.PcBalance, pcOffer));
        }

        tenders.Add(new Tender(PaymentMethod.Wallet, walletOffer, "m"));
        if (cash > 0)
        {
            tenders.Add(new Tender(PaymentMethod.Cash, cash));
        }

        return tenders;
    }

    private static PayableLine[] Rc81Lines() => [Line(CafeFixture.Ps5, false, 123_750), Snack(12_350)];

    private static PayableLine Line(string group, bool pc, long net) => new(group, pc, true, net);

    private static PayableLine Snack(long net) => new(null, false, false, net);

    private static Tender Wallet(long amount) => new(PaymentMethod.Wallet, amount, "m");

    private static Tender Cash(long amount) => new(PaymentMethod.Cash, amount);

    private static AllocationResult Pay(PayableLine[] lines, Tender[] tenders, RoundingPolicy rounding, long balance = 500_000)
    {
        return PaymentAllocator.Allocate(lines, tenders, rounding, [new MemberWallet("m", balance, 0)]);
    }

    private static long[] Shares(AllocationResult result, int tenderIndex)
    {
        var amounts = new long[result.CounterRemainderByLine.Count];
        foreach (var share in result.Shares.Where(share => share.TenderIndex == tenderIndex))
        {
            amounts[share.LineIndex] += share.AmountToman;
        }

        return amounts;
    }

    private static long Hour(RateCard card, string group, string station, int hour)
    {
        var start = T.At(2026, 6, 2, hour, 0);
        return SessionPricer.Price(
            CafeFixture.Session("s", CafeFixture.Play("a", start, start.AddHours(1), station, group, 1)),
            card).TotalAmountToman;
    }

    private static SessionCharge PlaySeconds(int activeSeconds, int pauses)
    {
        var start = T.At(2026, 6, 2, 10, 0);
        var pauseList = new List<PauseInterval>();
        var cursor = 0;
        var piece = activeSeconds / Math.Max(1, pauses + 1);
        for (var i = 0; i < pauses; i++)
        {
            cursor += piece;
            pauseList.Add(new PauseInterval(TehranTime.AddSeconds(start, cursor), TehranTime.AddSeconds(start, cursor + 30)));
            cursor += 30;
        }

        var end = TehranTime.AddSeconds(start, activeSeconds + (30L * pauses));
        return CafeFixture.Price(new Session("s", [CafeFixture.Play("a", start, end, "ps5-1", CafeFixture.Ps5, 1)], pauseList));
    }
}
