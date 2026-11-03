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

public enum DiscountStacking
{
    /// <summary>
    /// Default. Apply the better of customer-level and birthday (one of them), plus one code.
    /// Both are computed on the original discount base, then capped so the payable total cannot go below zero.
    /// </summary>
    BestLevelOrBirthdayPlusCode = 0,

    /// <summary>
    /// Level and birthday are both computed on the original base (each floored to a toman) and added, plus one code.
    /// This is the opt-in closest to Game Timer, which subtracted both percentages.
    /// </summary>
    SumLevelBirthdayAndCode = 1,

    /// <summary>
    /// Best of level or birthday is taken off the original base, then the code is applied to what remains of the code base.
    /// </summary>
    BestLevelOrBirthdayThenCompoundCode = 2,
}

public enum DiscountScope
{
    /// <summary>Default. Discounts apply to time charges. Buffet and fixed services stay full price.</summary>
    TimeChargesOnly = 0,

    /// <summary>Discounts apply to time, buffet, and services together.</summary>
    WholeInvoice = 1,
}

public sealed record DiscountPolicy(DiscountStacking Stacking, DiscountScope Scope)
{
    public static DiscountPolicy Default { get; } = new(DiscountStacking.BestLevelOrBirthdayPlusCode, DiscountScope.TimeChargesOnly);
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

public sealed record CustomerPricing(string? LevelName, int LevelPercent, bool IsBirthday, int BirthdayPercent);

/// <summary>
/// One code per invoice. Set either <see cref="Percent"/> or <see cref="FixedToman"/>, not both.
/// <see cref="ValidFrom"/> is inclusive and <see cref="ValidUntil"/> is exclusive, compared to the invoice instant.
/// Group and weekday restrictions apply to time lines (a restricted code never discounts buffet).
/// </summary>
public sealed record DiscountCode(
    string Code,
    int Percent,
    long FixedToman,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    IReadOnlySet<string>? AllowedGroupIds,
    IReadOnlySet<DayOfWeek>? AllowedWeekdays)
{
    public static DiscountCode PercentOff(
        string code,
        int percent,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        IReadOnlySet<string>? allowedGroupIds = null,
        IReadOnlySet<DayOfWeek>? allowedWeekdays = null)
    {
        return new DiscountCode(code, percent, 0, validFrom, validUntil, allowedGroupIds, allowedWeekdays);
    }

    public static DiscountCode FixedOff(
        string code,
        long fixedToman,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        IReadOnlySet<string>? allowedGroupIds = null,
        IReadOnlySet<DayOfWeek>? allowedWeekdays = null)
    {
        return new DiscountCode(code, 0, fixedToman, validFrom, validUntil, allowedGroupIds, allowedWeekdays);
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

        var timeGross = request.TimeCharges.Sum(line => line.AmountToman);
        var buffetGross = request.Buffet.Sum(line => line.TotalToman);
        var servicesGross = request.Services.Sum(line => line.AmountToman);
        var gross = timeGross + buffetGross + servicesGross;
        var scopeBase = request.Discount.Scope == DiscountScope.WholeInvoice ? gross : timeGross;

        var discountRows = new List<(string Description, long Amount)>();
        var customer = request.Customer;
        var level = customer?.LevelPercent ?? 0;
        var birthday = customer is { IsBirthday: true } ? customer.BirthdayPercent : 0;
        if (level is < 0 or > 100 || birthday is < 0 or > 100)
        {
            throw new BillingException("invalid_policy", "Discount percents must be between 0 and 100.");
        }

        if (request.Discount.Stacking == DiscountStacking.SumLevelBirthdayAndCode)
        {
            AddPercent(discountRows, scopeBase, level, LevelDescription(customer, level));
            AddPercent(discountRows, scopeBase, birthday, $"Birthday discount ({birthday}%)");
        }
        else
        {
            var best = Math.Max(level, birthday);
            var useBirthday = birthday >= level && birthday > 0 && customer is { IsBirthday: true };
            var description = useBirthday
                ? $"Birthday discount ({birthday}%)"
                : LevelDescription(customer, best);
            AddPercent(discountRows, scopeBase, best, description);
        }

        string? rejected = null;
        if (request.Code is DiscountCode code)
        {
            rejected = TryAddCode(request, code, scopeBase, timeGross, discountRows);
        }

        var cap = scopeBase;
        TrimToCap(discountRows, cap);
        var discountTotal = discountRows.Sum(row => row.Amount);
        var net = gross - discountTotal;

        long payable;
        long roundingDelta;
        if (request.Rounding.Scope == RoundingScope.WholeInvoice || !request.Rounding.Enabled)
        {
            (payable, roundingDelta) = MoneyMath.Round(net, request.Rounding);
        }
        else
        {
            var onTime = Math.Min(discountTotal, timeGross);
            var timeNet = timeGross - onTime;
            var otherNet = buffetGross + servicesGross - (discountTotal - onTime);
            var (roundedTime, delta) = MoneyMath.Round(timeNet, request.Rounding);
            roundingDelta = delta;
            payable = roundedTime + otherNet;
        }

        var lines = new List<InvoiceLine>();
        lines.AddRange(request.TimeCharges.Select(line => new InvoiceLine(InvoiceLineKind.Time, line.Description, line.AmountToman)));
        lines.AddRange(request.Buffet.Select(line => new InvoiceLine(InvoiceLineKind.Buffet, line.Name, line.TotalToman)));
        lines.AddRange(request.Services.Select(line => new InvoiceLine(InvoiceLineKind.Service, line.Name, line.AmountToman)));
        lines.AddRange(discountRows.Select(row => new InvoiceLine(InvoiceLineKind.Discount, row.Description, -row.Amount)));
        if (roundingDelta != 0)
        {
            lines.Add(new InvoiceLine(InvoiceLineKind.Rounding, "Rounding", roundingDelta));
        }

        return new Invoice(lines, timeGross, buffetGross, servicesGross, discountTotal, net, roundingDelta, payable, rejected);
    }

    private static string? TryAddCode(
        InvoiceRequest request,
        DiscountCode code,
        long scopeBase,
        long timeGross,
        List<(string Description, long Amount)> rows)
    {
        if (string.IsNullOrWhiteSpace(code.Code))
        {
            throw new BillingException("invalid_policy", "Discount code is required.");
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
            return DiscountRejection.OutsideWindow;
        }

        if (code.ValidUntil is DateTimeOffset validUntil && request.IssuedAt >= validUntil)
        {
            return DiscountRejection.OutsideWindow;
        }

        var restricted = code.AllowedGroupIds is { Count: > 0 } || code.AllowedWeekdays is { Count: > 0 };
        var eligibleTime = restricted ? EligibleTime(request.TimeCharges, code) : timeGross;
        if (restricted && eligibleTime == 0)
        {
            return DiscountRejection.NotEligible;
        }

        var codeBase = restricted ? eligibleTime : scopeBase;
        long amount;
        if (request.Discount.Stacking == DiscountStacking.BestLevelOrBirthdayThenCompoundCode)
        {
            var customerAlready = rows.Sum(row => row.Amount);
            var shared = scopeBase == 0 ? 0 : Math.Min(codeBase, scopeBase);
            var customerOnCode = scopeBase == 0 ? 0 : customerAlready * shared / scopeBase;
            var reduced = Math.Max(0, codeBase - Math.Min(codeBase, customerOnCode));
            amount = code.Percent > 0
                ? MoneyMath.PercentOf(reduced, code.Percent)
                : Math.Min(code.FixedToman, reduced);
        }
        else
        {
            amount = code.Percent > 0
                ? MoneyMath.PercentOf(codeBase, code.Percent)
                : Math.Min(code.FixedToman, codeBase);
        }

        if (amount > 0)
        {
            var label = code.Percent > 0
                ? $"Discount code {code.Code} ({code.Percent}%)"
                : $"Discount code {code.Code}";
            rows.Add((label, amount));
        }

        return null;
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

    private static void AddPercent(List<(string Description, long Amount)> rows, long basis, int percent, string description)
    {
        var amount = MoneyMath.PercentOf(basis, percent);
        if (amount > 0)
        {
            rows.Add((description, amount));
        }
    }

    private static string LevelDescription(CustomerPricing? customer, int percent)
    {
        var name = string.IsNullOrWhiteSpace(customer?.LevelName) ? "Customer" : customer!.LevelName;
        return $"{name} discount ({percent}%)";
    }

    private static void TrimToCap(List<(string Description, long Amount)> rows, long cap)
    {
        var over = rows.Sum(row => row.Amount) - cap;
        if (over <= 0)
        {
            return;
        }

        for (var i = rows.Count - 1; i >= 0 && over > 0; i--)
        {
            var cut = Math.Min(over, rows[i].Amount);
            rows[i] = (rows[i].Description, rows[i].Amount - cut);
            over -= cut;
        }

        rows.RemoveAll(row => row.Amount == 0);
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
