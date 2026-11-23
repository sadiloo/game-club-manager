using GameClub.Billing;
using Xunit;
using Xunit.Abstractions;

namespace GameClub.Billing.Tests;

/// <summary>Derives B5' expected numbers; kept for base-vs-head comparison runs.</summary>
public sealed class B5MoneyOnlyProbeTests(ITestOutputHelper output)
{
    static BillingPolicy G(int granularity)
        => new(granularity, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);

    static RateCard Flat()
        => new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();

    [Fact]
    public void dump_money_only_on_head_engine()
    {
        var start = T.At(2026, 6, 2, 22, 0);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", start, start.AddHours(1), "st", "g", PlayMode.Offline, 1)]),
            Flat(),
            policy: G(1));
        var st206 = AccountSettlement.Settle(
            charge,
            new PcAccount("m", 56_000),
            G(1),
            discount: new SessionDiscount(CodePercent: 50, ValidFrom: T.At(2026, 6, 2, 22, 40)));
        output.WriteLine($"rc_206 graceAt={st206.Grace?.At:o} waived={st206.Grace?.WaivedToman}");

        var s = T.At(2026, 6, 2, 23, 50);
        var end = T.At(2026, 6, 3, 0, 20);
        var ch207 = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", s, end, "st", PilotSeed.PcVipGroupId, PlayMode.Online, 1, StationKind.Pc)]),
            PilotSeed.CreateCard(),
            policy: G(1));
        var st207 = AccountSettlement.Settle(ch207, new PcAccount("m", 20_499), G(1));
        output.WriteLine(
            $"rc_207 writeOff={st207.WriteOffToman} money={st207.MoneyConsumedToman} bal={st207.UpdatedAccount.MoneyBalanceToman}");

        var d0 = T.At(2026, 6, 2, 10, 0);
        var ch92 = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", d0, d0.AddHours(1).AddSeconds(31), "st", "g", PlayMode.Offline, 1)]),
            Flat(),
            policy: G(15));
        var r92 = CustomerPlay.Settle(
            ch92,
            new CustomerAccount(
                "m",
                WalletSettings.Shared with { ApplyDiscountsToPc = true },
                new WalletPocket(4_458, 0),
                new WalletPocket(0, 0)),
            billing: G(15),
            pricing: new CustomerPricing("Titan", 28, null, 0),
            code: new DiscountCode("C", 35, 6_268, d0.AddSeconds(22 * 60 + 19), null, null, null),
            issuedAt: d0.AddHours(1).AddSeconds(31));
        var st92 = r92.Settlement;
        output.WriteLine(
            $"r9_2 money={st92.MoneyConsumedToman} disc={st92.DiscountToman} omitted={st92.OmittedToman} writeOff={st92.WriteOffToman}");

        var sv = T.At(2026, 6, 2, 23, 25);
        var ch213 = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", sv, sv.AddMinutes(34), "st", PilotSeed.PcVipGroupId, PlayMode.Online, 1)]),
            PilotSeed.CreateCard(),
            policy: G(1));
        var r213 = CustomerPlay.Settle(
            ch213,
            new CustomerAccount(
                "m",
                WalletSettings.Shared with { ApplyDiscountsToPc = true },
                new WalletPocket(348, 0),
                new WalletPocket(0, 0)),
            billing: G(1),
            pricing: new CustomerPricing("L", 30, null, 0),
            code: DiscountCode.FixedOff("F", 9_000, validFrom: T.At(2026, 6, 2, 23, 55)),
            issuedAt: sv.AddMinutes(34));
        var st213 = r213.Settlement;
        output.WriteLine(
            $"rc_213vip money={st213.MoneyConsumedToman} disc={st213.DiscountToman} grace={st213.Grace?.WaivedToman} writeOff={st213.WriteOffToman} omitted={st213.OmittedToman}");

        var sd = T.At(2026, 6, 2, 10, 0);
        var ch213d = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", sd, sd.AddMinutes(34), "st", PilotSeed.PcVipGroupId, PlayMode.Online, 1)]),
            PilotSeed.CreateCard(),
            policy: G(1));
        var r213d = CustomerPlay.Settle(
            ch213d,
            new CustomerAccount(
                "m",
                WalletSettings.Shared with { ApplyDiscountsToPc = true },
                new WalletPocket(348, 0),
                new WalletPocket(0, 0)),
            billing: G(1),
            pricing: new CustomerPricing("L", 30, null, 0),
            code: DiscountCode.FixedOff("F", 9_000, validFrom: sd.AddMinutes(30)),
            issuedAt: sd.AddMinutes(34));
        var st213d = r213d.Settlement;
        output.WriteLine(
            $"rc_213day money={st213d.MoneyConsumedToman} disc={st213d.DiscountToman} grace={st213d.Grace?.WaivedToman} writeOff={st213d.WriteOffToman} omitted={st213d.OmittedToman}");
    }
}