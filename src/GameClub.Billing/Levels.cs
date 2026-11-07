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
/// Thresholds are whole hours and must be strictly ascending. Percents are 0–100 and names are unique.
/// The first threshold may be greater than 0; <see cref="Resolve"/> then returns null below that threshold (0% level discount).
/// Otherwise <see cref="Resolve"/> returns the highest row reached.
/// <see cref="Default"/> is the editable Game Timer 4.3 sample for a new branch.
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
    /// Game Timer 4.3 sample ladder for a new branch (editable by the cafe).
    /// </summary>
    public static LevelLadder Default { get; } = Create(
    [
        new CustomerLevel("Bronze", 50, 5),
        new CustomerLevel("Silver", 100, 10),
        new CustomerLevel("Gold", 150, 15),
        new CustomerLevel("Crystal", 200, 20),
        new CustomerLevel("Master", 250, 24),
        new CustomerLevel("Champion", 300, 26),
        new CustomerLevel("Titan", 350, 28),
        new CustomerLevel("Legend", 400, 30),
    ]);

    /// <summary>Same object as <see cref="Default"/>.</summary>
    public static LevelLadder ExampleSeed => Default;

    public CustomerLevel? Resolve(long lifetimeHours)
    {
        if (lifetimeHours < 0)
        {
            throw new BillingException(BillingErrors.InvalidLevel, "Lifetime hours cannot be negative.");
        }

        if (_levels[0].MinimumLifetimeHours > lifetimeHours)
        {
            return null;
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
