namespace GameClub.Billing;

public enum RoundingDirection
{
    /// <summary>Default. 138,700 with step 5,000 becomes 140,000.</summary>
    Up = 0,
    Down = 1,

    /// <summary>Exact halves round up (away from zero for a non-negative amount).</summary>
    Nearest = 2,
}

public enum RoundingScope
{
    /// <summary>Round the net time charge after discounts. Buffet and services are added afterwards.</summary>
    TimeChargesOnly = 0,

    /// <summary>Round the whole invoice after discounts.</summary>
    WholeInvoice = 1,
}

/// <summary>
/// Branch rounding (PA-8). Off unless a branch turns it on. Applied after discounts
/// and before split payment. The difference is its own invoice line and may be negative
/// when the direction is down or nearest-down. A zero net is left at zero — it is not
/// lifted to the step. Game Timer's nearest-100-toman round is not applied.
/// </summary>
public sealed record RoundingPolicy(bool Enabled, long StepToman, RoundingDirection Direction, RoundingScope Scope)
{
    public static RoundingPolicy Off { get; } = new(false, 1_000, RoundingDirection.Up, RoundingScope.WholeInvoice);

    public static RoundingPolicy Create(
        long stepToman,
        RoundingDirection direction = RoundingDirection.Up,
        RoundingScope scope = RoundingScope.WholeInvoice)
    {
        if (stepToman <= 0)
        {
            throw new BillingException("invalid_policy", "Rounding step must be a positive number of toman.");
        }

        return new RoundingPolicy(true, stepToman, direction, scope);
    }
}

/// <summary>Which invoice parts a discount code may reduce. Combine flags, for example time and buffet together.</summary>
[Flags]
public enum ChargeScope
{
    None = 0,
    Time = 1,
    Buffet = 2,
    Services = 4,
}

public enum BirthdayWindowKind
{
    /// <summary>Default. The birthday month and day only.</summary>
    ExactDay = 0,

    /// <summary>A span of <see cref="BirthdayWindow.Days"/> calendar days. See <see cref="BirthdayWindowAnchor"/>.</summary>
    DayWindow = 1,

    /// <summary>Every day of the Jalali birth month. All of Mehr is month 7, not a Gregorian month.</summary>
    BirthMonth = 2,
}

public enum BirthdayWindowAnchor
{
    /// <summary>The birthday and the next <c>Days - 1</c> days. Seven days is the birthday plus the six days after it.</summary>
    OnAndAfter = 0,

    /// <summary>
    /// An N-day span that contains the birthday. An odd N is centered.
    /// An even N puts the extra day after the birthday.
    /// </summary>
    Around = 1,
}

/// <summary>
/// Per-cafe birthday discount window on the Solar Hijri calendar.
/// Whole month means the Jalali birth month, such as all of Mehr.
/// A birthday on Esfand 30 is observed on Esfand 29 when that Jalali year is not a leap year.
/// The date passed to Includes is the Tehran civil date of the service.
/// </summary>
public sealed record BirthdayWindow(BirthdayWindowKind Kind, int Days = 1, BirthdayWindowAnchor Anchor = BirthdayWindowAnchor.OnAndAfter)
{
    public static BirthdayWindow ExactDay { get; } = new(BirthdayWindowKind.ExactDay);

    public static BirthdayWindow WholeMonth { get; } = new(BirthdayWindowKind.BirthMonth);

    public static BirthdayWindow DaysOnAndAfter(int days) => new(BirthdayWindowKind.DayWindow, days, BirthdayWindowAnchor.OnAndAfter);

    public static BirthdayWindow DaysAround(int days) => new(BirthdayWindowKind.DayWindow, days, BirthdayWindowAnchor.Around);

    public void EnsureValid()
    {
        if (Kind == BirthdayWindowKind.DayWindow && Days < 1)
        {
            throw new BillingException("invalid_policy", "A birthday day-window must be at least 1 day.");
        }
    }

    public bool Includes(JalaliDate birthDate, DateOnly tehranDate)
    {
        EnsureValid();
        var on = JalaliDate.FromGregorian(tehranDate);
        if (Kind == BirthdayWindowKind.BirthMonth)
        {
            return on.Month == birthDate.Month;
        }

        if (Kind == BirthdayWindowKind.ExactDay)
        {
            return on == birthDate.ObservedIn(on.Year);
        }

        for (var year = on.Year - 1; year <= on.Year + 1; year++)
        {
            var birthday = birthDate.ObservedIn(year).ToGregorian();
            var before = Anchor == BirthdayWindowAnchor.Around ? (Days - 1) / 2 : 0;
            var after = Anchor == BirthdayWindowAnchor.Around ? Days - 1 - before : Days - 1;
            if (tehranDate >= birthday.AddDays(-before) && tehranDate <= birthday.AddDays(after))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Level and birthday percents are added, then capped (default 100), and applied to time charges only.
/// The code is a separate discount on <see cref="DiscountCode.Scope"/>.
/// </summary>
public sealed record DiscountPolicy(int MaxCombinedPercent, BirthdayWindow Birthday)
{
    public const int DefaultMaxCombinedPercent = 100;

    public static DiscountPolicy Default { get; } = new(DefaultMaxCombinedPercent, BirthdayWindow.ExactDay);

    public DiscountPolicy()
        : this(DefaultMaxCombinedPercent, BirthdayWindow.ExactDay)
    {
    }
}

public static class DiscountRejection
{
    public const string OutsideWindow = "outside_window";
    public const string NotEligible = "not_eligible";
}

public sealed record TimeChargeLine(
    string Description,
    long AmountToman,
    string? GroupId,
    DateOnly? ServiceDate,
    DayOfWeek? Weekday)
{
    public static IReadOnlyList<TimeChargeLine> FromSession(SessionCharge charge)
    {
        return charge.Lines
            .Select(line =>
            {
                var date = TehranTime.Date(line.Start);
                var start = TehranTime.TimeOfDay(line.Start);
                var end = TehranTime.TimeOfDay(line.End);
                return new TimeChargeLine(
                    $"{line.GroupId} {line.Mode} x{line.PlayerCount} {start.Hour:00}:{start.Minute:00}-{end.Hour:00}:{end.Minute:00}",
                    line.AmountToman,
                    line.GroupId,
                    date,
                    date.DayOfWeek);
            })
            .ToArray();
    }
}

public sealed record BuffetLine(string ItemId, string Name, int Quantity, long UnitPriceToman)
{
    public long TotalToman => checked((long)Quantity * UnitPriceToman);
}

public sealed record ServiceLine(string ServiceId, string Name, long AmountToman);

/// <summary>
/// Level percent and birthday percent are separate. <see cref="BirthDate"/> is a Solar Hijri date
/// (year, month, and day). Convert a Gregorian birth date with <see cref="JalaliDate.FromGregorian"/>
/// before storing it. The cafe's <see cref="BirthdayWindow"/> decides whether a Tehran service date qualifies.
/// A null birth date means no birthday discount.
/// </summary>
public sealed record CustomerPricing(string? LevelName, int LevelPercent, JalaliDate? BirthDate, int BirthdayPercent);

/// <summary>
/// One code per invoice. Set either <see cref="Percent"/> or <see cref="FixedToman"/>, not both.
/// <see cref="ValidFrom"/> is inclusive and <see cref="ValidUntil"/> is exclusive, compared to the invoice instant.
/// <see cref="Scope"/> chooses time, buffet, services, or a combination. The default is time only.
/// A group restriction filters time lines. Buffet and services in scope are discounted only when the invoice has at least one time line in an allowed group; otherwise the code does not apply. Weekday restrictions still filter time lines only.
/// </summary>
public sealed record DiscountCode(
    string Code,
    int Percent,
    long FixedToman,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    IReadOnlySet<string>? AllowedGroupIds,
    IReadOnlySet<DayOfWeek>? AllowedWeekdays,
    ChargeScope Scope = ChargeScope.Time)
{
    public static DiscountCode PercentOff(
        string code,
        int percent,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        IReadOnlySet<string>? allowedGroupIds = null,
        IReadOnlySet<DayOfWeek>? allowedWeekdays = null,
        ChargeScope scope = ChargeScope.Time)
    {
        return new DiscountCode(code, percent, 0, validFrom, validUntil, allowedGroupIds, allowedWeekdays, scope);
    }

    public static DiscountCode FixedOff(
        string code,
        long fixedToman,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        IReadOnlySet<string>? allowedGroupIds = null,
        IReadOnlySet<DayOfWeek>? allowedWeekdays = null,
        ChargeScope scope = ChargeScope.Time)
    {
        return new DiscountCode(code, 0, fixedToman, validFrom, validUntil, allowedGroupIds, allowedWeekdays, scope);
    }
}

public sealed record InvoiceRequest(
    IReadOnlyList<TimeChargeLine> TimeCharges,
    IReadOnlyList<BuffetLine> Buffet,
    IReadOnlyList<ServiceLine> Services,
    CustomerPricing? Customer,
    DiscountCode? Code,
    DiscountPolicy Discount,
    RoundingPolicy Rounding,
    DateTimeOffset IssuedAt);

public enum InvoiceLineKind
{
    Time = 0,
    Buffet = 1,
    Service = 2,
    Discount = 3,
    Rounding = 4,
}

public sealed record InvoiceLine(InvoiceLineKind Kind, string Description, long AmountToman);

public sealed record Invoice(
    IReadOnlyList<InvoiceLine> Lines,
    long TimeGrossToman,
    long BuffetGrossToman,
    long ServicesGrossToman,
    long DiscountToman,
    long NetBeforeRoundingToman,
    long RoundingToman,
    long PayableToman,
    string? RejectedCode)
{
    public long GrossToman => TimeGrossToman + BuffetGrossToman + ServicesGrossToman;
}

public static class InvoiceCalculator
{
    public static Invoice Calculate(InvoiceRequest request)
    {
        TehranTime.Ensure(request.IssuedAt);
        Validate(request);

        request.Discount.Birthday.EnsureValid();
        if (request.Discount.MaxCombinedPercent is < 0 or > 100)
        {
            throw new BillingException("invalid_policy", "The combined level and birthday cap must be between 0 and 100 percent.");
        }

        var timeGross = request.TimeCharges.Sum(line => line.AmountToman);
        var buffetGross = request.Buffet.Sum(line => line.TotalToman);
        var servicesGross = request.Services.Sum(line => line.AmountToman);
        var gross = timeGross + buffetGross + servicesGross;

        var (levelAmount, birthdayAmount) = CustomerDiscounts(request, timeGross);
        long codeOnTime = 0;
        long codeOnBuffet = 0;
        long codeOnServices = 0;
        string? rejected = null;
        string? codeLabel = null;
        if (request.Code is DiscountCode code)
        {
            (rejected, codeLabel, codeOnTime, codeOnBuffet, codeOnServices) = EvaluateCode(request, code, timeGross, buffetGross, servicesGross);
        }

        TrimExcess(ref codeOnTime, ref birthdayAmount, ref levelAmount, timeGross);
        CapAt(ref codeOnBuffet, buffetGross);
        CapAt(ref codeOnServices, servicesGross);

        var codeAmount = codeOnTime + codeOnBuffet + codeOnServices;
        var discountTotal = levelAmount + birthdayAmount + codeAmount;
        var discountOnTime = levelAmount + birthdayAmount + codeOnTime;
        var discountOnOther = codeOnBuffet + codeOnServices;
        var net = gross - discountTotal;

        long payable;
        long roundingDelta;
        if (request.Rounding.Scope == RoundingScope.WholeInvoice || !request.Rounding.Enabled)
        {
            (payable, roundingDelta) = MoneyMath.Round(net, request.Rounding);
        }
        else
        {
            var timeNet = timeGross - discountOnTime;
            var otherNet = buffetGross + servicesGross - discountOnOther;
            var (roundedTime, delta) = MoneyMath.Round(timeNet, request.Rounding);
            roundingDelta = delta;
            payable = roundedTime + otherNet;
        }

        var lines = new List<InvoiceLine>();
        lines.AddRange(request.TimeCharges.Select(line => new InvoiceLine(InvoiceLineKind.Time, line.Description, line.AmountToman)));
        lines.AddRange(request.Buffet.Select(line => new InvoiceLine(InvoiceLineKind.Buffet, line.Name, line.TotalToman)));
        lines.AddRange(request.Services.Select(line => new InvoiceLine(InvoiceLineKind.Service, line.Name, line.AmountToman)));
        AddDiscountLine(lines, LevelDescription(request.Customer, request.Customer?.LevelPercent ?? 0), levelAmount);
        AddDiscountLine(lines, $"Birthday discount ({request.Customer?.BirthdayPercent ?? 0}%)", birthdayAmount);
        if (codeAmount > 0 && codeLabel is not null)
        {
            lines.Add(new InvoiceLine(InvoiceLineKind.Discount, codeLabel, -codeAmount));
        }

        if (roundingDelta != 0)
        {
            lines.Add(new InvoiceLine(InvoiceLineKind.Rounding, "Rounding", roundingDelta));
        }

        return new Invoice(lines, timeGross, buffetGross, servicesGross, discountTotal, net, roundingDelta, payable, rejected);
    }

    private static (long Level, long Birthday) CustomerDiscounts(InvoiceRequest request, long timeGross)
    {
        var customer = request.Customer;
        var level = customer?.LevelPercent ?? 0;
        var birthdayPercent = customer?.BirthdayPercent ?? 0;
        if (level is < 0 or > 100 || birthdayPercent is < 0 or > 100)
        {
            throw new BillingException("invalid_policy", "Discount percents must be between 0 and 100.");
        }

        var cap = request.Discount.MaxCombinedPercent;
        long eligible = 0;
        long other = timeGross;
        if (customer?.BirthDate is JalaliDate birth && birthdayPercent > 0)
        {
            eligible = 0;
            other = 0;
            foreach (var line in request.TimeCharges)
            {
                var date = line.ServiceDate ?? TehranTime.Date(request.IssuedAt);
                if (request.Discount.Birthday.Includes(birth, date))
                {
                    eligible += line.AmountToman;
                }
                else
                {
                    other += line.AmountToman;
                }
            }
        }

        var levelApplied = Math.Min(cap, level);
        var levelAmount = MoneyMath.PercentOf(other, levelApplied);
        long birthdayAmount = 0;
        if (eligible > 0)
        {
            var sum = level + birthdayPercent;
            if (sum <= cap)
            {
                levelAmount += MoneyMath.PercentOf(eligible, level);
                birthdayAmount = MoneyMath.PercentOf(eligible, birthdayPercent);
            }
            else
            {
                var combined = MoneyMath.PercentOf(eligible, cap);
                var levelShare = sum == 0 ? 0 : combined * level / sum;
                levelAmount += levelShare;
                birthdayAmount = combined - levelShare;
            }
        }

        return (levelAmount, birthdayAmount);
    }

    private static (string? Rejected, string? Label, long Time, long Buffet, long Services) EvaluateCode(
        InvoiceRequest request,
        DiscountCode code,
        long timeGross,
        long buffetGross,
        long servicesGross)
    {
        if (string.IsNullOrWhiteSpace(code.Code))
        {
            throw new BillingException("invalid_policy", "Discount code is required.");
        }

        if (code.Scope == ChargeScope.None)
        {
            throw new BillingException("invalid_policy", "A discount code must name at least one of time, buffet, or services.");
        }

        if (code.Percent < 0 || code.FixedToman < 0 || code.Percent > 100)
        {
            throw new BillingException("invalid_policy", "Discount code percent must be 0–100 and the fixed amount cannot be negative.");
        }

        if (code.Percent > 0 && code.FixedToman > 0)
        {
            throw new BillingException("invalid_policy", "A discount code is either a percent or a fixed toman amount.");
        }

        if (code.ValidFrom is DateTimeOffset from)
        {
            TehranTime.Ensure(from);
        }

        if (code.ValidUntil is DateTimeOffset until)
        {
            TehranTime.Ensure(until);
        }

        if (code.ValidFrom is DateTimeOffset validFrom && request.IssuedAt < validFrom)
        {
            return (DiscountRejection.OutsideWindow, null, 0, 0, 0);
        }

        if (code.ValidUntil is DateTimeOffset validUntil && request.IssuedAt >= validUntil)
        {
            return (DiscountRejection.OutsideWindow, null, 0, 0, 0);
        }

        var groupRestricted = code.AllowedGroupIds is { Count: > 0 };
        if (groupRestricted && !HasMatchingGroupLine(request.TimeCharges, code))
        {
            return (DiscountRejection.NotEligible, null, 0, 0, 0);
        }

        var restricted = groupRestricted || code.AllowedWeekdays is { Count: > 0 };
        var timeBase = code.Scope.HasFlag(ChargeScope.Time)
            ? restricted ? EligibleTime(request.TimeCharges, code) : timeGross
            : 0;
        var buffetBase = code.Scope.HasFlag(ChargeScope.Buffet) ? buffetGross : 0;
        var serviceBase = code.Scope.HasFlag(ChargeScope.Services) ? servicesGross : 0;
        var codeBase = timeBase + buffetBase + serviceBase;
        if (codeBase == 0)
        {
            return (DiscountRejection.NotEligible, null, 0, 0, 0);
        }

        var amount = code.Percent > 0
            ? MoneyMath.PercentOf(codeBase, code.Percent)
            : Math.Min(code.FixedToman, codeBase);
        long onTime = 0;
        long onBuffet = 0;
        long onServices = 0;
        Allocate(amount, codeBase, timeBase, buffetBase, serviceBase, ref onTime, ref onBuffet, ref onServices);
        var label = code.Percent > 0
            ? $"Discount code {code.Code} ({code.Percent}%)"
            : $"Discount code {code.Code}";
        return (null, label, onTime, onBuffet, onServices);
    }

    private static void Allocate(
        long amount,
        long codeBase,
        long timeBase,
        long buffetBase,
        long serviceBase,
        ref long onTime,
        ref long onBuffet,
        ref long onServices)
    {
        if (amount <= 0 || codeBase <= 0)
        {
            return;
        }

        var parts = new List<(int Kind, long Base)>();
        if (timeBase > 0)
        {
            parts.Add((0, timeBase));
        }

        if (buffetBase > 0)
        {
            parts.Add((1, buffetBase));
        }

        if (serviceBase > 0)
        {
            parts.Add((2, serviceBase));
        }

        long allocated = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            var share = i == parts.Count - 1 ? amount - allocated : amount * parts[i].Base / codeBase;
            allocated += share;
            switch (parts[i].Kind)
            {
                case 0:
                    onTime = share;
                    break;
                case 1:
                    onBuffet = share;
                    break;
                default:
                    onServices = share;
                    break;
            }
        }
    }

    private static bool HasMatchingGroupLine(IReadOnlyList<TimeChargeLine> lines, DiscountCode code)
    {
        foreach (var line in lines)
        {
            if (line.GroupId is not null && code.AllowedGroupIds!.Contains(line.GroupId))
            {
                return true;
            }
        }

        return false;
    }

    private static long EligibleTime(IReadOnlyList<TimeChargeLine> lines, DiscountCode code)
    {
        long sum = 0;
        foreach (var line in lines)
        {
            if (code.AllowedGroupIds is { Count: > 0 } groups && (line.GroupId is null || !groups.Contains(line.GroupId)))
            {
                continue;
            }

            if (code.AllowedWeekdays is { Count: > 0 } days && (line.Weekday is null || !days.Contains(line.Weekday.Value)))
            {
                continue;
            }

            sum += line.AmountToman;
        }

        return sum;
    }

    private static void AddDiscountLine(List<InvoiceLine> lines, string description, long amount)
    {
        if (amount > 0)
        {
            lines.Add(new InvoiceLine(InvoiceLineKind.Discount, description, -amount));
        }
    }

    private static string LevelDescription(CustomerPricing? customer, int percent)
    {
        var name = string.IsNullOrWhiteSpace(customer?.LevelName) ? "Customer" : customer!.LevelName;
        return $"{name} discount ({percent}%)";
    }

    private static void CapAt(ref long amount, long cap)
    {
        if (amount > cap)
        {
            amount = Math.Max(0, cap);
        }
    }

    /// <summary>Reduces the first bucket, then the second, then the third, until their sum is within <paramref name="cap"/>.</summary>
    private static void TrimExcess(ref long cutFirst, ref long cutSecond, ref long cutThird, long cap)
    {
        var over = cutFirst + cutSecond + cutThird - cap;
        if (over <= 0)
        {
            return;
        }

        var fromFirst = Math.Min(over, cutFirst);
        cutFirst -= fromFirst;
        over -= fromFirst;
        var fromSecond = Math.Min(over, Math.Max(0, cutSecond));
        cutSecond -= fromSecond;
        over -= fromSecond;
        cutThird -= Math.Min(over, Math.Max(0, cutThird));
    }

    private static void Validate(InvoiceRequest request)
    {
        foreach (var line in request.TimeCharges)
        {
            if (line.AmountToman < 0)
            {
                throw new BillingException("invalid_money", "Time charge lines cannot be negative.");
            }
        }

        foreach (var line in request.Buffet)
        {
            if (string.IsNullOrWhiteSpace(line.ItemId))
            {
                throw new BillingException("invalid_invoice", "Buffet item id is required.");
            }

            if (line.Quantity <= 0 || line.UnitPriceToman < 0)
            {
                throw new BillingException("invalid_money", $"Buffet item '{line.ItemId}' has an invalid quantity or price.");
            }
        }

        foreach (var line in request.Services)
        {
            if (line.AmountToman < 0)
            {
                throw new BillingException("invalid_money", $"Service '{line.ServiceId}' cannot be negative.");
            }
        }
    }
}
