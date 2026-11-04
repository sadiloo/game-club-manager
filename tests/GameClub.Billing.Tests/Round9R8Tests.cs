using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class Round9R8Tests
{
    static BillingPolicy G5()
        => new(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);

    static RateCard Flat()
        => new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();

    static long Parts(Settlement st)
        => st.PackageCoveredToman + st.MoneyConsumedToman + st.DiscountToman
            + (st.Grace?.WaivedToman ?? 0) + st.WriteOffToman + st.UncoveredToman + st.OmittedToman;

    [Fact]
    public void r8_1_minimal_repro_packages_then_money_pins_debit_discount_and_parts()
    {
        var policy = G5();
        var start = T.At(2026, 6, 2, 22, 0);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", start, start.AddMinutes(63), "st", "g", PlayMode.Offline, 1)]),
            Flat(),
            policy: policy);
        var result = CustomerPlay.Settle(
            charge,
            new CustomerAccount(
                "m",
                WalletSettings.Shared with { ApplyDiscountsToPc = true },
                new WalletPocket(1_000_000, 0),
                new WalletPocket(0, 0),
                [new TimePackage("p", 8, 200_000)]),
            policy: new AccountPolicy(ConsumptionOrder.PackagesThenMoney),
            billing: policy,
            pricing: new CustomerPricing("Titan", 28, null, 0),
            code: new DiscountCode("FX", 0, 5_000, T.At(2026, 6, 2, 23, 1), null, null, null),
            issuedAt: start.AddMinutes(63));
        var st = result.Settlement;
        Assert.Equal(charge.TotalAmountToman, Parts(st));
        Assert.Equal(70_241, st.MoneyConsumedToman);
        Assert.Equal(34_260, st.DiscountToman);
        Assert.Equal(0, st.WriteOffToman);
        Assert.Null(st.Grace);
        Assert.Null(st.Alert);
    }

    [Fact]
    public void r8_1_conservation_corpus_without_settlement_plug()
    {
        var rng = new Random(707);
        var flat = Flat();
        var titan = new CustomerPricing("Titan", 28, null, 0);
        var birth = JalaliDate.FromGregorian(new LocalDate(2026, 6, 6));
        var granularities = new[] { 1, 5, 15 };
        for (var i = 0; i < 2_000; i++)
        {
            var g = granularities[rng.Next(granularities.Length)];
            var policy = new BillingPolicy(g, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
            var start = TehranTime.AddSeconds(T.At(2026, 6, 5, 20, 0), rng.Next(0, 8 * 3600));
            var end = TehranTime.AddSeconds(start, rng.Next(30, 2 * 3600));
            var charge = SessionPricer.Price(
                new Session("x", [new PlaySegment("a", start, end, "st", "g", PlayMode.Offline, 1)]),
                flat,
                policy: policy);
            var balance = rng.Next(0, 300_000);
            var packages = rng.Next(3) == 0
                ? new[] { new TimePackage("p", rng.Next(1, 60), 200_000) }
                : Array.Empty<TimePackage>();
            var order = rng.Next(2) == 0
                ? ConsumptionOrder.PackagesThenMoney
                : ConsumptionOrder.MoneyThenPackages;
            var spec = new SessionDiscount(
                LevelPercent: rng.Next(3) == 0 ? 0 : 28,
                BirthdayPercent: rng.Next(5) == 0 ? 20 : 0,
                CodePercent: rng.Next(3) == 0 ? 0 : rng.Next(1, 30),
                FixedToman: rng.Next(4) == 0 ? rng.Next(1, 20_000) : 0,
                BirthDate: rng.Next(5) == 0 ? birth : null,
                ValidFrom: rng.Next(2) == 0 ? TehranTime.AddSeconds(start, rng.Next(0, 3600)) : null);
            var st = AccountSettlement.Settle(
                charge,
                new PcAccount("m", balance, packages),
                new AccountPolicy(order),
                policy,
                discount: spec);
            Assert.Equal(charge.TotalAmountToman, Parts(st));
            Assert.True(st.MoneyConsumedToman >= 0);
            Assert.True(st.WriteOffToman >= 0);
            Assert.True(st.DiscountToman >= 0);
        }
    }

    [Fact]
    public void r8_3_station_lookup_is_case_insensitive_and_rejects_internal_space_aliases()
    {
        var group = new StationGroup("pcg", "PC", StationKind.Pc);
        var dir = new StationDirectory([group], [new Station("PC1", "one", "pcg")]);
        Assert.True(dir.TryGetGroup("pc1", out _));
        Assert.True(dir.TryGetGroup("  Pc1 ", out _));

        var exDup = Assert.Throws<BillingException>(() =>
            new StationDirectory([group], [new Station("pc1", "a", "pcg"), new Station("PC1", "b", "pcg")]));
        Assert.Equal(BillingErrors.InvalidTariff, exDup.Code);

        var start = T.At(2026, 6, 2, 10, 0);
        var badSpace = new Session("bad", [
            new PlaySegment("a", start, start.AddMinutes(5), "pc 1", "pcg", PlayMode.Offline, 1),
        ]);
        Assert.Throws<BillingException>(() => SessionPricer.Price(badSpace, Flat(), stations: dir));
    }

    [Fact]
    public void r8_3_station_registration_normalizes_arabic_letters_and_digits()
    {
        var group = new StationGroup("pcg", "PC", StationKind.Pc);
        var dir = new StationDirectory(
            [group],
            [new Station(" \u06431 ", "kaf", "pcg")]);
        Assert.True(dir.TryGetGroup("\u06A91", out _));
    }

    [Fact]
    public void segment_group_must_match_station_directory()
    {
        var stations = new StationDirectory(
            [new StationGroup("vip", "VIP", StationKind.Pc), new StationGroup("norm", "Normal", StationKind.Pc)],
            [new Station("pc-1", "PC", "vip")]);
        var start = T.At(2026, 6, 2, 10, 0);
        var session = new Session("x", [
            new PlaySegment("a", start, start.AddMinutes(10), "pc-1", "norm", PlayMode.Offline, 1),
        ]);
        var ex = Assert.Throws<BillingException>(() => SessionPricer.Price(session, Flat(), stations: stations));
        Assert.Equal(BillingErrors.InvalidSession, ex.Code);
    }
}
