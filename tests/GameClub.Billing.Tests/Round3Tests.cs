using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class Round3Tests
{
    [Fact]
    public void Prepaid_pc_invoice_with_pilot_rounding_keeps_the_exact_debit()
    {
        var card = new RateCardBuilder()
            .AddBase(PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, PilotSeed.PcNormalHourly)
            .Build();
        var start = T.At(2026, 6, 2, 10, 0);
        var charge = SessionPricer.Price(new Session("pc", [
            new PlaySegment("a", start, start.AddMinutes(3), "pc-1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, StationKind.Pc),
        ]), card);
        Assert.Equal(4_000, charge.TotalAmountToman);

        var account = new CustomerAccount(
            "m",
            WalletSettings.Shared,
            new WalletPocket(4_000, 0),
            new WalletPocket(0, 0),
            []);
        var play = CustomerPlay.Settle(charge, account);

        Assert.Equal(4_000, play.Settlement.MoneyConsumedToman);
        Assert.Equal(4_000, play.ChargeInvoice!.PayableToman);
        Assert.Equal(0, play.ChargeInvoice.RoundingToman);
        Assert.Equal(0, play.Account.PcMoneyToman);
    }

    [Fact]
    public void At_100_000_the_second_minute_is_1666_and_at_110_000_it_is_1834()
    {
        var card = new RateCardBuilder()
            .AddBase("flat", PlayMode.Offline, 1, 100_000)
            .Build();
        var one = MoneyMath.ChargeForMinutes(100_000, 1, RateRemainderMode.Nearest);
        var two = MoneyMath.ChargeForMinutes(100_000, 2, RateRemainderMode.Nearest);
        Assert.Equal(1_667, one);
        Assert.Equal(3_333, two);
        var next = two - one;
        Assert.Equal(1_666, next);
        var one110 = MoneyMath.ChargeForMinutes(110_000, 1, RateRemainderMode.Nearest);
        var two110 = MoneyMath.ChargeForMinutes(110_000, 2, RateRemainderMode.Nearest);
        Assert.Equal(1_833, one110);
        Assert.Equal(1_834, two110 - one110);

        var budget = one + next - 1;
        var start = T.At(2026, 6, 2, 10, 0);
        var plan = PrepaidPlanner.ForAmount(start, budget, "a", "st", "flat", PlayMode.Offline, 1, card);
        Assert.Equal(1, plan.Grace!.WaivedToman);
        Assert.Equal(next, plan.Lines.Sum(line => line.AmountToman) - one);
        Assert.Equal(0, plan.UnusedToman);

        var played = SessionPricer.Price(new Session("x", [
            new PlaySegment("a", plan.Start, plan.End, "st", "flat", PlayMode.Offline, 1),
        ]), card);
        var settled = AccountSettlement.Settle(played, new PcAccount("m", budget, []));
        Assert.Equal(plan.ConsumedToman, settled.MoneyConsumedToman);
        Assert.Equal(plan.Grace.WaivedToman, settled.Grace!.WaivedToman);
        Assert.Equal(0, settled.UncoveredToman);

        var rng = new Random(7);
        for (var i = 0; i < 40; i++)
        {
            var amount = rng.Next(1, 80_000);
            var at = TehranTime.AddSeconds(start, rng.Next(0, 3_600));
            var priced = PrepaidPlanner.ForAmount(at, amount, "a", "st", "flat", PlayMode.Offline, 1, card);
            var session = SessionPricer.Price(new Session("x", [
                new PlaySegment("a", priced.Start, priced.End, "st", "flat", PlayMode.Offline, 1),
            ]), card);
            var result = AccountSettlement.Settle(session, new PcAccount("m", amount, []));
            Assert.Equal(priced.ConsumedToman, result.MoneyConsumedToman);
            Assert.Equal(priced.Grace?.WaivedToman ?? 0, result.Grace?.WaivedToman ?? 0);
            Assert.Equal(0, result.UncoveredToman);
        }
    }

    [Fact]
    public void A_pause_on_a_pc_is_rejected_and_a_console_pause_is_priced()
    {
        var stations = new StationDirectory(
            [
                new StationGroup(PilotSeed.PcNormalGroupId, "PC", StationKind.Pc),
                new StationGroup(CafeFixture.Ps5, "PS5", StationKind.Console),
            ],
            [
                new Station("pc-1", "PC 1", PilotSeed.PcNormalGroupId),
                new Station("ps5-1", "PS5 1", CafeFixture.Ps5),
            ]);
        var start = T.At(2026, 6, 2, 10, 0);
        var pause = new PauseInterval(start.AddMinutes(2), start.AddMinutes(3));
        var pc = new Session("pc", [
            new PlaySegment("a", start, start.AddMinutes(10), "pc-1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1),
        ], [pause]);
        var error = Assert.Throws<BillingException>(() => SessionPricer.Price(pc, PilotSeed.CreateCard(), stations: stations));
        Assert.Equal(BillingErrors.PcPause, error.Code);

        var marked = new Session("pc", [
            new PlaySegment("a", start, start.AddMinutes(10), "pc-1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, StationKind.Pc),
        ], [pause]);
        var markedError = Assert.Throws<BillingException>(() => SessionPricer.Price(marked, PilotSeed.CreateCard()));
        Assert.Equal(BillingErrors.PcPause, markedError.Code);

        var console = new Session("ps", [
            new PlaySegment("a", start, start.AddMinutes(10), "ps5-1", CafeFixture.Ps5, PlayMode.Offline, 1),
        ], [pause]);
        var priced = SessionPricer.Price(console, CafeFixture.Card(), stations: stations);
        Assert.Equal(9, priced.TotalBilledMinutes);
    }
}
