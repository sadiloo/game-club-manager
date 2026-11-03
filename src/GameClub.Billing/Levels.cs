namespace GameClub.Billing;

/// <summary>
/// One row of a cafe-edited level ladder. <see cref="MinimumLifetimeHours"/> is the
/// lifetime play time required to reach this row. The percent is 0–100.
/// </summary>
public sealed record CustomerLevel(string Name, long MinimumLifetimeHours, int DiscountPercent);

/// <summary>
/// The cafe's customer levels. Thresholds must be strictly ascending. The level
/// for a visit is the highest row whose threshold is less than or equal to the
/// lifetime hours the caller passes in. Hours below the first threshold have no level.
/// <see cref="ExampleSeed"/> is an optional starting ladder, not a tariff from Game Timer.
/// </summary>
public sealed class LevelLadder
{
    private readonly CustomerLevel[] _levels;

    private LevelLadder(CustomerLevel[] levels) => _levels = levels;

    public IReadOnlyList<CustomerLevel> Levels => _levels;

    public static LevelLadder Create(IReadOnlyList<CustomerLevel> levels)
    {
        if (levels is null || levels.Count == 0)
        {
            throw new BillingException("invalid_level", "A level ladder needs at least one level.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        long? previous = null;
        foreach (var level in levels)
        {
            if (string.IsNullOrWhiteSpace(level.Name))
            {
                throw new BillingException("invalid_level", "A level name is required.");
            }

            if (!names.Add(level.Name))
            {
                throw new BillingException("invalid_level", $"Duplicate level name '{level.Name}'.");
            }

            if (level.MinimumLifetimeHours < 0)
            {
                throw new BillingException("invalid_level", $"Level '{level.Name}' has a negative hour threshold.");
            }

            if (level.DiscountPercent is < 0 or > 100)
            {
                throw new BillingException("invalid_level", $"Level '{level.Name}' percent must be between 0 and 100.");
            }

            if (previous is long prior && level.MinimumLifetimeHours <= prior)
            {
                throw new BillingException(
                    "invalid_level",
                    $"Level '{level.Name}' threshold {level.MinimumLifetimeHours} is not strictly above {prior}.");
            }

            previous = level.MinimumLifetimeHours;
        }

        return new LevelLadder(levels.ToArray());
    }

    /// <summary>
    /// Optional seed in the shape of the product brief (Bronze 5% … Titan 28%).
    /// The hour thresholds are placeholders so a new cafe has something to edit.
    /// They were not in the Game Timer export.
    /// </summary>
    public static LevelLadder ExampleSeed { get; } = Create(
    [
        new CustomerLevel("Bronze", 0, 5),
        new CustomerLevel("Silver", 30, 8),
        new CustomerLevel("Gold", 80, 12),
        new CustomerLevel("Platinum", 150, 16),
        new CustomerLevel("Diamond", 300, 20),
        new CustomerLevel("Titan", 500, 28),
    ]);

    public CustomerLevel? Resolve(long lifetimeHours)
    {
        if (lifetimeHours < 0)
        {
            throw new BillingException("invalid_level", "Lifetime hours cannot be negative.");
        }

        CustomerLevel? match = null;
        foreach (var level in _levels)
        {
            if (level.MinimumLifetimeHours <= lifetimeHours)
            {
                match = level;
            }
            else
            {
                break;
            }
        }

        return match;
    }
}
