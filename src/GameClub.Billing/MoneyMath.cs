namespace GameClub.Billing;

/// <summary>How a partial billing block (default: one minute) is rounded.</summary>
public enum PartialUnitMode
{
    /// <summary>Any leftover time inside a block is billed as a whole block. Default.</summary>
    Ceiling = 0,

    /// <summary>Leftover time inside a block is not billed.</summary>
    Floor = 1,

    /// <summary>A leftover of at least half a block rounds up. An exact half rounds up.</summary>
    Nearest = 2,
}

/// <summary>
/// How the fractional toman of (billed minutes × hourly rate) / 60 is resolved.
/// Money is never floating-point; the remainder is the numerator modulo 60.
/// </summary>
public enum RateRemainderMode
{
    Floor = 0,
    Ceiling = 1,

    /// <summary>Remainder of 30 or more (half a toman or more) rounds away from zero. Default.</summary>
    Nearest = 2,
}

/// <summary>
/// Billing granularity and the two integer rounding choices that turn a duration
/// into toman. Defaults: 1-minute blocks, partial blocks round up, fractional
/// toman rounds half up.
/// </summary>
public sealed record BillingPolicy(int GranularityMinutes, PartialUnitMode PartialUnits, RateRemainderMode Remainder)
{
    public static BillingPolicy Default { get; } = new(1, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);

    public void EnsureValid()
    {
        if (GranularityMinutes <= 0)
        {
            throw new BillingException("invalid_policy", "Billing granularity must be at least 1 minute.");
        }
    }
}

public static class MoneyMath
{
    public static long BilledMinutes(long activeSeconds, BillingPolicy policy)
    {
        policy.EnsureValid();
        if (activeSeconds < 0)
        {
            throw new BillingException("invalid_time", "Active time cannot be negative.");
        }

        if (activeSeconds == 0)
        {
            return 0;
        }

        var blockSeconds = (long)policy.GranularityMinutes * 60L;
        var blocks = activeSeconds / blockSeconds;
        var leftover = activeSeconds % blockSeconds;
        var extra = policy.PartialUnits switch
        {
            PartialUnitMode.Ceiling => leftover > 0,
            PartialUnitMode.Floor => false,
            PartialUnitMode.Nearest => leftover * 2 >= blockSeconds,
            _ => throw new BillingException("invalid_policy", $"Unknown partial-unit mode '{policy.PartialUnits}'."),
        };
        if (extra)
        {
            blocks++;
        }

        return checked(blocks * policy.GranularityMinutes);
    }

    /// <summary>
    /// Charge for a whole number of billed minutes at an hourly rate, in toman.
    /// Equivalent to dividing <c>hourly × minutes</c> by 60 with the given remainder mode.
    /// </summary>
    public static long ChargeForMinutes(long hourlyRateToman, long billedMinutes, RateRemainderMode remainder)
    {
        if (hourlyRateToman < 0)
        {
            throw new BillingException("invalid_money", "Hourly rate cannot be negative.");
        }

        if (billedMinutes < 0)
        {
            throw new BillingException("invalid_time", "Billed minutes cannot be negative.");
        }

        if (hourlyRateToman == 0 || billedMinutes == 0)
        {
            return 0;
        }

        var numerator = checked(hourlyRateToman * billedMinutes);
        var quotient = numerator / 60;
        var rem = numerator % 60;
        return remainder switch
        {
            RateRemainderMode.Floor => quotient,
            RateRemainderMode.Ceiling => rem == 0 ? quotient : quotient + 1,
            RateRemainderMode.Nearest => rem >= 30 ? quotient + 1 : quotient,
            _ => throw new BillingException("invalid_policy", $"Unknown remainder mode '{remainder}'."),
        };
    }

    /// <summary>Floor of <c>amount × percent / 100</c>. Percents at or above 100 return the whole amount.</summary>
    public static long PercentOf(long amount, int percent)
    {
        if (amount < 0)
        {
            throw new BillingException("invalid_money", "Cannot take a percent of a negative amount.");
        }

        if (percent <= 0 || amount == 0)
        {
            return 0;
        }

        if (percent >= 100)
        {
            return amount;
        }

        return checked(amount * percent) / 100;
    }

    /// <summary>
    /// Largest multiple of the policy granularity whose charge does not exceed
    /// <paramref name="budgetToman"/>. Used for remaining-time estimates and prepaid money.
    /// </summary>
    public static long MaxAffordableMinutes(long hourlyRateToman, long budgetToman, BillingPolicy policy)
    {
        policy.EnsureValid();
        if (budgetToman < 0)
        {
            throw new BillingException("invalid_money", "Budget cannot be negative.");
        }

        if (budgetToman == 0 || hourlyRateToman <= 0)
        {
            return 0;
        }

        long upperBlocks;
        try
        {
            upperBlocks = checked((budgetToman * 60L) / hourlyRateToman + 2);
        }
        catch (OverflowException)
        {
            upperBlocks = 2_000_000;
        }

        long lo = 0;
        long hi = upperBlocks;
        long best = 0;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var minutes = checked(mid * policy.GranularityMinutes);
            var charge = ChargeForMinutes(hourlyRateToman, minutes, policy.Remainder);
            if (charge <= budgetToman)
            {
                best = minutes;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }

    public static (long Rounded, long Delta) Round(long amount, RoundingPolicy policy)
    {
        if (amount < 0)
        {
            throw new BillingException("invalid_money", "Cannot round a negative amount.");
        }

        if (!policy.Enabled || amount == 0)
        {
            return (amount, 0);
        }

        if (policy.StepToman <= 0)
        {
            throw new BillingException("invalid_policy", "Rounding step must be a positive number of toman.");
        }

        var step = policy.StepToman;
        var rem = amount % step;
        if (rem == 0)
        {
            return (amount, 0);
        }

        // Half up without multiplying the remainder (a huge custom step must not overflow).
        var halfRoundsUp = rem >= (step / 2) + (step % 2 == 0 ? 0 : 1);
        long rounded = policy.Direction switch
        {
            RoundingDirection.Up => checked(amount + (step - rem)),
            RoundingDirection.Down => amount - rem,
            RoundingDirection.Nearest => halfRoundsUp
                ? checked(amount + (step - rem))
                : amount - rem,
            _ => throw new BillingException("invalid_policy", $"Unknown rounding direction '{policy.Direction}'."),
        };

        return (rounded, rounded - amount);
    }
}
