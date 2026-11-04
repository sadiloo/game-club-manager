using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class LevelLadderTests
{
    [Fact]
    public void Default_ladder_is_bronze_through_titan_and_a_cafe_can_replace_it()
    {
        var seed = LevelLadder.Default;
        Assert.Same(seed, LevelLadder.ExampleSeed);
        Assert.Same(seed, GameTimerReference.ExampleLevelSeed);
        Assert.Equal(
            new[] { "Bronze", "Silver", "Gold", "Platinum", "Diamond", "Titan" },
            seed.Levels.Select(level => level.Name).ToArray());
        Assert.Equal(new long[] { 0, 30, 80, 150, 300, 500 }, seed.Levels.Select(level => level.MinimumLifetimeHours).ToArray());
        Assert.Equal(new[] { 5, 8, 12, 16, 20, 28 }, seed.Levels.Select(level => level.DiscountPercent).ToArray());
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

        var added = LevelLadder.Create(cafe.Levels.Append(new CustomerLevel("طلایی", 80, 25)).ToArray());
        Assert.Equal(4, added.Levels.Count);
        Assert.Equal(25, added.Resolve(80)!.DiscountPercent);
        var removed = LevelLadder.Create([cafe.Levels[0]]);
        Assert.Equal("تازه‌وارد", Assert.Single(removed.Levels).Name);

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
        var notZero = Assert.Throws<BillingException>(() => LevelLadder.Create(
            [new CustomerLevel("only", 5, 10)]));
        Assert.Equal(BillingErrors.InvalidLevel, notZero.Code);

        var descending = Assert.Throws<BillingException>(() => LevelLadder.Create(
        [
            new CustomerLevel("A", 0, 5),
            new CustomerLevel("B", 10, 8),
            new CustomerLevel("C", 10, 9),
        ]));
        Assert.Equal(BillingErrors.InvalidLevel, descending.Code);

        var duplicate = Assert.Throws<BillingException>(() => LevelLadder.Create(
        [
            new CustomerLevel("A", 0, 5),
            new CustomerLevel("A", 10, 8),
        ]));
        Assert.Equal(BillingErrors.InvalidLevel, duplicate.Code);

        var blank = Assert.Throws<BillingException>(() => LevelLadder.Create(
            [new CustomerLevel("  ", 0, 5)]));
        Assert.Equal(BillingErrors.InvalidLevel, blank.Code);

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
