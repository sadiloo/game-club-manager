using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class LevelLadderTests
{
    [Fact]
    public void Example_seed_is_labeled_and_a_custom_ladder_follows_lifetime_hours()
    {
        var seed = LevelLadder.ExampleSeed;
        Assert.Equal("Bronze", seed.Resolve(0)!.Name);
        Assert.Equal(5, seed.Resolve(29)!.DiscountPercent);
        Assert.Equal("Silver", seed.Resolve(30)!.Name);
        Assert.Equal(28, seed.Resolve(500)!.DiscountPercent);

        var cafe = LevelLadder.Create(
        [
            new CustomerLevel("تازه‌وارد", 0, 0),
            new CustomerLevel("همیشگی", 10, 12),
            new CustomerLevel("ویژه", 40, 20),
        ]);
        Assert.Equal(0, cafe.Resolve(9)!.DiscountPercent);
        Assert.Equal("همیشگی", cafe.Resolve(10)!.Name);
        Assert.Equal(12, cafe.Resolve(39)!.DiscountPercent);
        Assert.Equal(20, cafe.Resolve(40)!.DiscountPercent);
        var lateStart = LevelLadder.Create([new CustomerLevel("only", 5, 10)]);
        Assert.Null(lateStart.Resolve(4));
        Assert.Equal(10, lateStart.Resolve(5)!.DiscountPercent);

        var resolved = cafe.Resolve(40)!;
        var invoice = InvoiceCalculator.Calculate(new InvoiceRequest(
            [new TimeChargeLine("time", 100_000, CafeFixture.Ps5, new LocalDate(2026, 6, 2), DayOfWeek.Tuesday)],
            [],
            [],
            new CustomerPricing(resolved.Name, resolved.DiscountPercent, null, 0),
            null,
            DiscountPolicy.Default,
            RoundingPolicy.Off,
            T.At(2026, 6, 2, 18, 0)));
        Assert.Equal(20_000, invoice.DiscountToman);
        Assert.Equal(80_000, invoice.PayableToman);
    }

    [Fact]
    public void Level_ladder_rejects_bad_thresholds_and_percents()
    {
        var descending = Assert.Throws<BillingException>(() => LevelLadder.Create(
        [
            new CustomerLevel("A", 10, 5),
            new CustomerLevel("B", 10, 8),
        ]));
        Assert.Equal("invalid_level", descending.Code);

        var percent = Assert.Throws<BillingException>(() => LevelLadder.Create(
            [new CustomerLevel("A", 0, 101)]));
        Assert.Equal("invalid_level", percent.Code);

        var negative = Assert.Throws<BillingException>(() => LevelLadder.Create(
            [new CustomerLevel("A", 0, -1)]));
        Assert.Equal("invalid_level", negative.Code);

        var hours = Assert.Throws<BillingException>(() => LevelLadder.ExampleSeed.Resolve(-1));
        Assert.Equal("invalid_level", hours.Code);

        var empty = Assert.Throws<BillingException>(() => LevelLadder.Create([]));
        Assert.Equal("invalid_level", empty.Code);
    }
}
