namespace GameClub.Billing;

public enum PaymentMethod
{
    Cash = 0,
    CardReader = 1,
    CardToCard = 2,
    Wallet = 3,

    /// <summary>The PC money balance. It can pay PC time lines only, and it is never rounded.</summary>
    PcBalance = 4,
}

/// <summary>
/// A member wallet. Gift and real credit are one spendable balance. The basis fields
/// keep the ratio from the last top-up so many small draws match one large draw.
/// Gift credit does not expire and does not go negative. Debt sits on real credit.
/// </summary>
public sealed record MemberWallet(
    string MemberId,
    long RealCreditToman,
    long GiftCreditToman,
    long BasisRealToman = 0,
    long BasisGiftToman = 0,
    long PositiveDrawnToman = 0,
    long GiftDrawnFromBasisToman = 0)
{
    public long BalanceToman => RealCreditToman + GiftCreditToman;

    public long DebtToman => BalanceToman < 0 ? -BalanceToman : 0;

    public WalletPocket ToPocket()
    {
        var untouched = PositiveDrawnToman == 0
            && GiftDrawnFromBasisToman == 0
            && BasisRealToman == 0
            && BasisGiftToman == 0;
        if (untouched)
        {
            return new WalletPocket(RealCreditToman, GiftCreditToman);
        }

        return new WalletPocket(
            RealCreditToman,
            GiftCreditToman,
            BasisRealToman,
            BasisGiftToman,
            PositiveDrawnToman,
            GiftDrawnFromBasisToman);
    }

    public static MemberWallet FromPocket(string memberId, WalletPocket pocket)
    {
        return new MemberWallet(
            memberId,
            pocket.RealToman,
            pocket.GiftToman,
            pocket.BasisRealToman,
            pocket.BasisGiftToman,
            pocket.PositiveDrawnToman,
            pocket.GiftDrawnFromBasisToman);
    }
}

public sealed record Tender(PaymentMethod Method, long AmountToman, string? MemberId = null);

public static class PaymentErrors
{
    public const string SumMismatch = "sum_mismatch";
    public const string NegativeAmount = "negative_amount";
    public const string WalletMissingMember = "wallet_missing_member";
    public const string UnknownWallet = "unknown_wallet";
    public const string InsufficientWallet = "insufficient_wallet";
}

public sealed record PaymentError(string Code, string Message);

public sealed record MethodTotal(PaymentMethod Method, long AmountToman);

public sealed record TenderResult(
    int Index,
    PaymentMethod Method,
    long AmountToman,
    string? MemberId,
    long GiftDrawnToman,
    long RealDrawnToman,
    bool Accepted);

public sealed record SplitPayment(
    bool IsValid,
    long PayableToman,
    long TenderedToman,
    long DifferenceToman,
    IReadOnlyList<PaymentError> Errors,
    IReadOnlyList<MethodTotal> MethodTotals,
    IReadOnlyList<TenderResult> Tenders,
    IReadOnlyList<MemberWallet> RemainingWallets);

public static class PaymentSplitter
{
    /// <summary>
    /// Splits a payable total. Wallet debt defaults to <see cref="DebtPolicy.None"/>:
    /// a tender that would push the pocket below zero is refused. Pass
    /// <see cref="DebtPolicy.Unlimited"/> or <see cref="DebtPolicy.Limited"/> when a
    /// branch has allowed console or table debt.
    /// </summary>
    public static SplitPayment Split(
        long payableToman,
        IReadOnlyList<Tender> tenders,
        IReadOnlyList<MemberWallet>? wallets = null,
        DebtPolicy? walletDebt = null)
    {
        if (payableToman < 0)
        {
            throw new BillingException(BillingErrors.InvalidMoney, "Payable total cannot be negative.");
        }

        if (tenders is null)
        {
            throw new BillingException(BillingErrors.InvalidPayment, "Tenders are required.");
        }

        walletDebt ??= DebtPolicy.None;
        var errors = new List<PaymentError>();
        var balances = new Dictionary<string, WalletPocket>(StringComparer.Ordinal);
        foreach (var wallet in wallets ?? [])
        {
            if (string.IsNullOrWhiteSpace(wallet.MemberId))
            {
                throw new BillingException(BillingErrors.InvalidPayment, "Wallet member id is required.");
            }

            if (wallet.GiftCreditToman < 0)
            {
                throw new BillingException(BillingErrors.InvalidMoney, $"Wallet '{wallet.MemberId}' has negative gift credit. Debt is carried on real credit.");
            }

            if (!Bcl.TryAdd(balances, wallet.MemberId, wallet.ToPocket()))
            {
                throw new BillingException(BillingErrors.InvalidPayment, $"Duplicate wallet '{wallet.MemberId}'.");
            }
        }

        long tendered = 0;
        var results = new List<TenderResult>(tenders.Count);
        for (var i = 0; i < tenders.Count; i++)
        {
            var tender = tenders[i];
            if (tender.AmountToman < 0)
            {
                errors.Add(new PaymentError(PaymentErrors.NegativeAmount, $"Tender {i} is negative."));
                results.Add(new TenderResult(i, tender.Method, tender.AmountToman, tender.MemberId, 0, 0, false));
                continue;
            }

            tendered = MoneyMath.CheckedAdd(tendered, tender.AmountToman);
            if (tender.Method != PaymentMethod.Wallet)
            {
                results.Add(new TenderResult(i, tender.Method, tender.AmountToman, tender.MemberId, 0, 0, true));
                continue;
            }

            if (tender.MemberId is not string memberId || string.IsNullOrWhiteSpace(memberId))
            {
                errors.Add(new PaymentError(PaymentErrors.WalletMissingMember, $"Tender {i} is a wallet payment without a member."));
                results.Add(new TenderResult(i, tender.Method, tender.AmountToman, tender.MemberId, 0, 0, false));
                continue;
            }

            if (!balances.TryGetValue(memberId, out var balance))
            {
                errors.Add(new PaymentError(PaymentErrors.UnknownWallet, $"No wallet for member '{memberId}'."));
                results.Add(new TenderResult(i, tender.Method, tender.AmountToman, memberId, 0, 0, false));
                continue;
            }

            if (!balance.TrySpend(tender.AmountToman, walletDebt, out var updated, out var giftDrawn, out var realDrawn))
            {
                errors.Add(new PaymentError(
                    PaymentErrors.InsufficientWallet,
                    $"Wallet '{tender.MemberId}' cannot cover {tender.AmountToman} toman."));
                results.Add(new TenderResult(i, tender.Method, tender.AmountToman, tender.MemberId, 0, 0, false));
                continue;
            }

            balances[memberId] = updated;
            results.Add(new TenderResult(i, tender.Method, tender.AmountToman, memberId, giftDrawn, realDrawn, true));
        }

        var difference = tendered - payableToman;
        if (difference != 0)
        {
            errors.Add(new PaymentError(
                PaymentErrors.SumMismatch,
                $"Tendered {tendered} toman against payable {payableToman} (difference {difference})."));
        }

        var totals = results
            .GroupBy(result => result.Method)
            .Select(group => new MethodTotal(group.Key, MoneyMath.CheckedSum(group.Select(result => result.AmountToman))))
            .OrderBy(total => total.Method)
            .ToArray();

        var remaining = new List<MemberWallet>(balances.Count);
        foreach (var pair in balances)
        {
            remaining.Add(MemberWallet.FromPocket(pair.Key, pair.Value));
        }

        remaining.Sort((left, right) => string.CompareOrdinal(left.MemberId, right.MemberId));

        return new SplitPayment(errors.Count == 0, payableToman, tendered, difference, errors, totals, results, remaining);
    }
}

/// <summary>
/// Real and gift credit of one pocket. Debt sits on real credit. Gift credit does not go negative.
/// Draws use a cumulative floor of <c>totalDrawn × initialGift / initialTotal</c> so a series of
/// small draws takes the same gift as one draw of the sum. Anything beyond the positive balance
/// is debt on real credit.
/// </summary>
public readonly record struct WalletPocket
{
    public WalletPocket(long realToman, long giftToman)
        : this(
            realToman,
            giftToman,
            realToman > 0 ? realToman : 0,
            giftToman > 0 ? giftToman : 0,
            0,
            0)
    {
    }

    public WalletPocket(
        long realToman,
        long giftToman,
        long basisRealToman,
        long basisGiftToman,
        long positiveDrawnToman,
        long giftDrawnFromBasisToman)
    {
        RealToman = realToman;
        GiftToman = giftToman;
        BasisRealToman = basisRealToman;
        BasisGiftToman = basisGiftToman;
        PositiveDrawnToman = positiveDrawnToman;
        GiftDrawnFromBasisToman = giftDrawnFromBasisToman;
    }

    public long RealToman { get; }

    public long GiftToman { get; }

    public long BasisRealToman { get; }

    public long BasisGiftToman { get; }

    public long PositiveDrawnToman { get; }

    public long GiftDrawnFromBasisToman { get; }

    public long TotalToman => RealToman + GiftToman;

    public long DebtToman => TotalToman < 0 ? -TotalToman : 0;

    /// <summary>
    /// Spends <paramref name="amount"/>. The positive part keeps the original gift ratio.
    /// Anything beyond that is debt on real credit, down to <paramref name="debt"/>.
    /// A spend that would pass the floor is rejected whole.
    /// </summary>
    public bool TrySpend(long amount, DebtPolicy debt, out WalletPocket updated, out long giftDrawn, out long realDrawn)
    {
        var capacity = Capacity(TotalToman, debt);
        if (amount < 0 || amount > capacity)
        {
            updated = this;
            giftDrawn = 0;
            realDrawn = 0;
            return false;
        }

        var positiveGift = GiftToman > 0 ? GiftToman : 0;
        var positiveReal = RealToman > 0 ? RealToman : 0;
        var positive = positiveGift + positiveReal;
        var fromPositive = amount < positive ? amount : positive;
        var debtPart = amount - fromPositive;
        var basis = BasisGiftToman + BasisRealToman;
        long giftTake;
        if (fromPositive == 0 || basis <= 0 || BasisGiftToman <= 0)
        {
            giftTake = positiveReal == 0 && positiveGift > 0 ? fromPositive : 0;
        }
        else if (BasisRealToman <= 0)
        {
            giftTake = fromPositive;
        }
        else
        {
            var totalAfter = PositiveDrawnToman + fromPositive;
            var giftShould = (totalAfter * BasisGiftToman) / basis;
            giftTake = giftShould - GiftDrawnFromBasisToman;
            if (giftTake < 0)
            {
                giftTake = 0;
            }

            if (giftTake > fromPositive)
            {
                giftTake = fromPositive;
            }

            if (giftTake > positiveGift)
            {
                giftTake = positiveGift;
            }
        }

        var realTake = fromPositive - giftTake + debtPart;
        if (GiftToman - giftTake < 0)
        {
            updated = this;
            giftDrawn = 0;
            realDrawn = 0;
            return false;
        }

        updated = new WalletPocket(
            RealToman - realTake,
            GiftToman - giftTake,
            BasisRealToman,
            BasisGiftToman,
            PositiveDrawnToman + fromPositive,
            GiftDrawnFromBasisToman + giftTake);
        giftDrawn = giftTake;
        realDrawn = realTake;
        return true;
    }

    private static long Capacity(long balance, DebtPolicy debt)
    {
        if (debt.MaxDebtToman is null)
        {
            return long.MaxValue;
        }

        if (debt.MaxDebtToman == 0)
        {
            return balance > 0 ? balance : 0;
        }

        var maxDebt = debt.MaxDebtToman.Value;
        if (balance >= 0)
        {
            return MoneyMath.CheckedAdd(balance, maxDebt);
        }

        var owed = -balance;
        return owed >= maxDebt ? 0 : maxDebt - owed;
    }
}

/// <summary>
/// One invoice line after grace and discounts. <see cref="NetToman"/> is the amount a payer may cover.
/// A PC balance may cover a line only when <see cref="IsPcTime"/> is true.
/// </summary>
public sealed record PayableLine(string? GroupId, bool IsPcTime, bool IsTimeCharge, long NetToman);

public sealed record LineShare(int LineIndex, int TenderIndex, PaymentMethod Method, long AmountToman);

/// <summary>
/// Exact balance shares, then one rounding of the counter remainder.
/// Wallet and PC amounts are never rounded. A zero counter remainder is not lifted to the step.
/// </summary>
public sealed record AllocationResult(
    bool IsValid,
    long ExactNetToman,
    long BalanceAppliedToman,
    long CounterExactToman,
    long RoundingToman,
    long PayableToman,
    IReadOnlyList<long> CounterRemainderByLine,
    IReadOnlyList<LineShare> Shares,
    IReadOnlyList<TenderResult> Tenders,
    IReadOnlyList<PaymentError> Errors,
    IReadOnlyList<MemberWallet> RemainingWallets);

/// <summary>
/// Pipeline after the invoice net is known: PC balance, then wallet, each pro rata by largest remainder
/// (a tie goes to the earlier line), then one rounding of what the counter still owes.
/// </summary>
public static class PaymentAllocator
{
    public static AllocationResult Allocate(
        IReadOnlyList<PayableLine> lines,
        IReadOnlyList<Tender> tenders,
        RoundingPolicy rounding,
        IReadOnlyList<MemberWallet>? wallets = null,
        DebtPolicy? walletDebt = null)
    {
        if (lines is null)
        {
            throw new BillingException(BillingErrors.InvalidInvoice, "Invoice lines are required.");
        }

        if (tenders is null)
        {
            throw new BillingException(BillingErrors.InvalidPayment, "Tenders are required.");
        }

        walletDebt ??= DebtPolicy.None;
        var remaining = new long[lines.Count];
        long exact = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].NetToman < 0)
            {
                throw new BillingException(BillingErrors.InvalidMoney, "A line net cannot be negative.");
            }

            remaining[i] = lines[i].NetToman;
            exact = MoneyMath.CheckedAdd(exact, lines[i].NetToman);
        }

        var pockets = new Dictionary<string, WalletPocket>(StringComparer.Ordinal);
        foreach (var wallet in wallets ?? [])
        {
            if (string.IsNullOrWhiteSpace(wallet.MemberId))
            {
                throw new BillingException(BillingErrors.InvalidPayment, "Wallet member id is required.");
            }

            if (wallet.GiftCreditToman < 0)
            {
                throw new BillingException(BillingErrors.InvalidMoney, $"Wallet '{wallet.MemberId}' has negative gift credit.");
            }

            if (!Bcl.TryAdd(pockets, wallet.MemberId, wallet.ToPocket()))
            {
                throw new BillingException(BillingErrors.InvalidPayment, $"Duplicate wallet '{wallet.MemberId}'.");
            }
        }

        var shares = new List<LineShare>();
        var results = new TenderResult[tenders.Count];
        var errors = new List<PaymentError>();
        var order = BalanceFirst(tenders);
        foreach (var index in order)
        {
            var tender = tenders[index];
            if (tender.AmountToman < 0)
            {
                errors.Add(new PaymentError(PaymentErrors.NegativeAmount, $"Tender {index} is negative."));
                results[index] = new TenderResult(index, tender.Method, tender.AmountToman, tender.MemberId, 0, 0, false);
                continue;
            }

            if (tender.Method is PaymentMethod.Cash or PaymentMethod.CardReader or PaymentMethod.CardToCard)
            {
                results[index] = new TenderResult(index, tender.Method, tender.AmountToman, tender.MemberId, 0, 0, true);
                continue;
            }

            if (tender.Method == PaymentMethod.PcBalance)
            {
                var drawn = AllocateProRata(lines, remaining, index, tender.Method, tender.AmountToman, PcTimeOnly, shares);
                results[index] = new TenderResult(index, tender.Method, drawn, tender.MemberId, 0, drawn, drawn > 0 || tender.AmountToman == 0);
                continue;
            }

            if (tender.Method != PaymentMethod.Wallet)
            {
                errors.Add(new PaymentError(PaymentErrors.NegativeAmount, $"Tender {index} has an unknown method."));
                results[index] = new TenderResult(index, tender.Method, tender.AmountToman, tender.MemberId, 0, 0, false);
                continue;
            }

            if (tender.MemberId is not string memberId || string.IsNullOrWhiteSpace(memberId))
            {
                errors.Add(new PaymentError(PaymentErrors.WalletMissingMember, $"Tender {index} is a wallet payment without a member."));
                results[index] = new TenderResult(index, tender.Method, tender.AmountToman, tender.MemberId, 0, 0, false);
                continue;
            }

            if (!pockets.TryGetValue(memberId, out var pocket))
            {
                errors.Add(new PaymentError(PaymentErrors.UnknownWallet, $"No wallet for member '{memberId}'."));
                results[index] = new TenderResult(index, tender.Method, tender.AmountToman, memberId, 0, 0, false);
                continue;
            }

            var offer = tender.AmountToman;
            var coverable = Coverable(lines, remaining, _ => true);
            var draw = offer < coverable ? offer : coverable;
            if (!pocket.TrySpend(draw, walletDebt, out var updated, out var gift, out var real))
            {
                errors.Add(new PaymentError(PaymentErrors.InsufficientWallet, $"Wallet '{memberId}' cannot cover {draw} toman."));
                results[index] = new TenderResult(index, tender.Method, tender.AmountToman, memberId, 0, 0, false);
                continue;
            }

            pockets[memberId] = updated;
            AllocateProRata(lines, remaining, index, tender.Method, draw, _ => true, shares);
            results[index] = new TenderResult(index, tender.Method, draw, memberId, gift, real, true);
        }

        long counterTime = 0;
        long counterOther = 0;
        var counterByLine = new long[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            counterByLine[i] = remaining[i];
            if (lines[i].IsTimeCharge)
            {
                counterTime = MoneyMath.CheckedAdd(counterTime, remaining[i]);
            }
            else
            {
                counterOther = MoneyMath.CheckedAdd(counterOther, remaining[i]);
            }
        }

        var counterExact = MoneyMath.CheckedAdd(counterTime, counterOther);
        long roundingDelta = 0;
        long counterRounded = counterExact;
        if (rounding.Enabled && counterExact > 0)
        {
            if (rounding.Scope == RoundingScope.TimeChargesOnly)
            {
                var (roundedTime, delta) = MoneyMath.Round(counterTime, rounding);
                roundingDelta = delta;
                counterRounded = MoneyMath.CheckedAdd(roundedTime, counterOther);
            }
            else
            {
                (counterRounded, roundingDelta) = MoneyMath.Round(counterExact, rounding);
            }
        }

        long balanceApplied = 0;
        long counterTendered = 0;
        foreach (var result in results)
        {
            if (result is null || !result.Accepted)
            {
                continue;
            }

            if (result.Method is PaymentMethod.Cash or PaymentMethod.CardReader or PaymentMethod.CardToCard)
            {
                counterTendered = MoneyMath.CheckedAdd(counterTendered, result.AmountToman);
            }
            else
            {
                balanceApplied = MoneyMath.CheckedAdd(balanceApplied, result.AmountToman);
            }
        }

        if (counterTendered != counterRounded)
        {
            errors.Add(new PaymentError(
                PaymentErrors.SumMismatch,
                $"Counter tenders {counterTendered} toman against rounded remainder {counterRounded}."));
        }

        var payable = MoneyMath.CheckedAdd(balanceApplied, counterRounded);
        var remainingWallets = new List<MemberWallet>();
        foreach (var pair in pockets)
        {
            remainingWallets.Add(MemberWallet.FromPocket(pair.Key, pair.Value));
        }

        remainingWallets.Sort((left, right) => string.CompareOrdinal(left.MemberId, right.MemberId));
        var tenderList = new List<TenderResult>();
        foreach (var result in results)
        {
            if (result is not null)
            {
                tenderList.Add(result);
            }
        }

        return new AllocationResult(
            errors.Count == 0,
            exact,
            balanceApplied,
            counterExact,
            roundingDelta,
            payable,
            counterByLine,
            shares,
            tenderList,
            errors,
            remainingWallets);
    }

    private static List<int> BalanceFirst(IReadOnlyList<Tender> tenders)
    {
        var pc = new List<int>();
        var wallet = new List<int>();
        var counter = new List<int>();
        for (var i = 0; i < tenders.Count; i++)
        {
            switch (tenders[i].Method)
            {
                case PaymentMethod.PcBalance:
                    pc.Add(i);
                    break;
                case PaymentMethod.Wallet:
                    wallet.Add(i);
                    break;
                default:
                    counter.Add(i);
                    break;
            }
        }

        pc.AddRange(wallet);
        pc.AddRange(counter);
        return pc;
    }

    private static bool PcTimeOnly(PayableLine line) => line.IsPcTime && line.IsTimeCharge;

    private static long Coverable(IReadOnlyList<PayableLine> lines, long[] remaining, Func<PayableLine, bool> allowed)
    {
        long total = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (allowed(lines[i]))
            {
                total = MoneyMath.CheckedAdd(total, remaining[i]);
            }
        }

        return total;
    }

    private static long AllocateProRata(
        IReadOnlyList<PayableLine> lines,
        long[] remaining,
        int tenderIndex,
        PaymentMethod method,
        long offer,
        Func<PayableLine, bool> allowed,
        List<LineShare> shares)
    {
        var eligible = new List<int>();
        long total = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (allowed(lines[i]) && remaining[i] > 0)
            {
                eligible.Add(i);
                total = MoneyMath.CheckedAdd(total, remaining[i]);
            }
        }

        if (offer <= 0 || total <= 0)
        {
            return 0;
        }

        var pay = offer < total ? offer : total;
        var floors = new long[lines.Count];
        var frac = new long[lines.Count];
        long assigned = 0;
        foreach (var index in eligible)
        {
            var weight = remaining[index];
            floors[index] = pay * weight / total;
            frac[index] = pay * weight % total;
            assigned += floors[index];
        }

        var left = pay - assigned;
        var order = new List<int>(eligible);
        order.Sort((leftIndex, rightIndex) =>
        {
            var byFraction = frac[rightIndex].CompareTo(frac[leftIndex]);
            return byFraction != 0 ? byFraction : leftIndex.CompareTo(rightIndex);
        });
        var cursor = 0;
        while (left > 0 && order.Count > 0)
        {
            var index = order[cursor % order.Count];
            if (floors[index] < remaining[index])
            {
                floors[index]++;
                left--;
            }

            cursor++;
            if (cursor > order.Count * (left + 2))
            {
                break;
            }
        }

        long drawn = 0;
        foreach (var index in eligible)
        {
            if (floors[index] <= 0)
            {
                continue;
            }

            remaining[index] -= floors[index];
            drawn += floors[index];
            shares.Add(new LineShare(index, tenderIndex, method, floors[index]));
        }

        return drawn;
    }
}
