namespace GameClub.Billing;

/// <summary>
/// One row of a cafe-edited level ladder. <see cref="MinimumLifetimeHours"/> is a
/// whole number of lifetime hours (a <c>long</c>, not a fraction of an hour).
/// The percent is 0–100.
/// </summary>
public sealed record CustomerLevel(string Name, long MinimumLifetimeHours, int DiscountPercent);

/// <summary>
/// The cafe's customer levels. An admin edits the ladder by passing a new list to
/// <see cref="Create"/>: change a name, the whole-hour threshold, or the percent, and add or remove rows.
/// The first level is 0 hours. Later thresholds are strictly higher. Percents are 0–100 and names are unique.
/// <see cref="Resolve"/> returns the highest row the customer has reached. With a valid ladder that is never null
/// for a non-negative hour count.
/// <see cref="Default"/> is the ladder a new branch starts with (Bronze 5% … Titan 28%). It is not a Game Timer tariff.
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
            throw new BillingException(BillingErrors.InvalidLevel, "A level ladder needs at least one level.");
        }

        if (levels[0].MinimumLifetimeHours != 0)
        {
            throw new BillingException(BillingErrors.InvalidLevel, "The first level must start at 0 hours.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        long? previous = null;
        foreach (var level in levels)
        {
            if (string.IsNullOrWhiteSpace(level.Name))
            {
                throw new BillingException(BillingErrors.InvalidLevel, "A level name is required.");
            }

            if (!names.Add(level.Name))
            {
                throw new BillingException(BillingErrors.InvalidLevel, $"Duplicate level name '{level.Name}'.");
            }

            if (level.MinimumLifetimeHours < 0)
            {
                throw new BillingException(BillingErrors.InvalidLevel, $"Level '{level.Name}' has a negative hour threshold.");
            }

            if (level.DiscountPercent is < 0 or > 100)
            {
                throw new BillingException(BillingErrors.InvalidLevel, $"Level '{level.Name}' percent must be between 0 and 100.");
            }

            if (previous is long prior && level.MinimumLifetimeHours <= prior)
            {
                throw new BillingException(
                    BillingErrors.InvalidLevel,
                    $"Level '{level.Name}' threshold {level.MinimumLifetimeHours} is not strictly above {prior}.");
            }

            previous = level.MinimumLifetimeHours;
        }

        return new LevelLadder(levels.ToArray());
    }

    /// <summary>
    /// Default ladder for a new branch: Bronze 5% at 0 hours, Silver 8% at 30, Gold 12% at 80,
    /// Platinum 16% at 150, Diamond 20% at 300, Titan 28% at 500. Replace it with <see cref="Create"/>
    /// when the cafe wants different rows. The hours are whole hours and are the product default, not a Game Timer export.
    /// </summary>
    public static LevelLadder Default { get; } = Create(
    [
        new CustomerLevel("Bronze", 0, 5),
        new CustomerLevel("Silver", 30, 8),
        new CustomerLevel("Gold", 80, 12),
        new CustomerLevel("Platinum", 150, 16),
        new CustomerLevel("Diamond", 300, 20),
        new CustomerLevel("Titan", 500, 28),
    ]);

    /// <summary>Same object as <see cref="Default"/>.</summary>
    public static LevelLadder ExampleSeed => Default;

    public CustomerLevel? Resolve(long lifetimeHours)
    {
        if (lifetimeHours < 0)
        {
            throw new BillingException(BillingErrors.InvalidLevel, "Lifetime hours cannot be negative.");
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
