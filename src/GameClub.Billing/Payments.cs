namespace GameClub.Billing;

public enum PaymentMethod
{
    Cash = 0,
    CardReader = 1,
    CardToCard = 2,
    Wallet = 3,
}

/// <summary>
/// A member wallet. Gift and real credit are one spendable balance: a draw takes
/// them in proportion (gift share floored, leftover toman assigned to real) and
/// the result still reports each part. Gift credit does not expire. A wallet
/// tender cannot go negative; PC-account debt is a separate balance.
/// </summary>
public sealed record MemberWallet(string MemberId, long RealCreditToman, long GiftCreditToman)
{
    public long BalanceToman => RealCreditToman + GiftCreditToman;

    public long DebtToman => BalanceToman < 0 ? -BalanceToman : 0;
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
    public static SplitPayment Split(
        long payableToman,
        IReadOnlyList<Tender> tenders,
        IReadOnlyList<MemberWallet>? wallets = null,
        DebtPolicy? walletDebt = null)
    {
        if (payableToman < 0)
        {
            throw new BillingException("invalid_money", "Payable total cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(tenders);
        walletDebt ??= DebtPolicy.Unlimited;
        var errors = new List<PaymentError>();
        var balances = new Dictionary<string, (long Real, long Gift)>(StringComparer.Ordinal);
        foreach (var wallet in wallets ?? [])
        {
            if (string.IsNullOrWhiteSpace(wallet.MemberId))
            {
                throw new BillingException("invalid_payment", "Wallet member id is required.");
            }

            if (wallet.GiftCreditToman < 0)
            {
                throw new BillingException("invalid_money", $"Wallet '{wallet.MemberId}' has negative gift credit. Debt is carried on real credit.");
            }

            if (!balances.TryAdd(wallet.MemberId, (wallet.RealCreditToman, wallet.GiftCreditToman)))
            {
                throw new BillingException("invalid_payment", $"Duplicate wallet '{wallet.MemberId}'.");
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

            tendered += tender.AmountToman;
            if (tender.Method != PaymentMethod.Wallet)
            {
                results.Add(new TenderResult(i, tender.Method, tender.AmountToman, tender.MemberId, 0, 0, true));
                continue;
            }

            if (string.IsNullOrWhiteSpace(tender.MemberId))
            {
                errors.Add(new PaymentError(PaymentErrors.WalletMissingMember, $"Tender {i} is a wallet payment without a member."));
                results.Add(new TenderResult(i, tender.Method, tender.AmountToman, tender.MemberId, 0, 0, false));
                continue;
            }

            if (!balances.TryGetValue(tender.MemberId, out var balance))
            {
                errors.Add(new PaymentError(PaymentErrors.UnknownWallet, $"No wallet for member '{tender.MemberId}'."));
                results.Add(new TenderResult(i, tender.Method, tender.AmountToman, tender.MemberId, 0, 0, false));
                continue;
            }

            var pocket = new WalletPocket(balance.Real, balance.Gift);
            if (!pocket.TrySpend(tender.AmountToman, walletDebt, out var updated, out var giftDrawn, out var realDrawn))
            {
                errors.Add(new PaymentError(
                    PaymentErrors.InsufficientWallet,
                    $"Wallet '{tender.MemberId}' cannot cover {tender.AmountToman} toman."));
                results.Add(new TenderResult(i, tender.Method, tender.AmountToman, tender.MemberId, 0, 0, false));
                continue;
            }

            balance = (updated.RealToman, updated.GiftToman);
            balances[tender.MemberId] = balance;
            results.Add(new TenderResult(i, tender.Method, tender.AmountToman, tender.MemberId, giftDrawn, realDrawn, true));
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
            .Select(group => new MethodTotal(group.Key, group.Sum(result => result.AmountToman)))
            .OrderBy(total => total.Method)
            .ToArray();

        var remaining = balances
            .Select(pair => new MemberWallet(pair.Key, pair.Value.Real, pair.Value.Gift))
            .OrderBy(wallet => wallet.MemberId, StringComparer.Ordinal)
            .ToArray();

        return new SplitPayment(errors.Count == 0, payableToman, tendered, difference, errors, totals, results, remaining);
    }
}

/// <summary>
/// Real and gift credit of one pocket. Debt sits on real credit. Gift credit does not go negative.
/// </summary>
public readonly record struct WalletPocket(long RealToman, long GiftToman)
{
    public long TotalToman => RealToman + GiftToman;

    public long DebtToman => TotalToman < 0 ? -TotalToman : 0;

    /// <summary>
    /// Spends <paramref name="amount"/>. The positive part is split in proportion
    /// (gift share floored, leftover toman on real). Anything beyond that is debt on real credit,
    /// down to <paramref name="debt"/>. A spend that would pass the floor is rejected whole.
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
        if (positive == 0 || positiveGift == 0 || positiveReal == 0)
        {
            giftDrawn = positiveReal == 0 ? fromPositive : 0;
            realDrawn = fromPositive - giftDrawn;
        }
        else
        {
            giftDrawn = fromPositive * positiveGift / positive;
            realDrawn = fromPositive - giftDrawn;
        }

        realDrawn += amount - fromPositive;
        updated = new WalletPocket(RealToman - realDrawn, GiftToman - giftDrawn);
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
            return checked(balance + maxDebt);
        }

        var owed = -balance;
        return owed >= maxDebt ? 0 : maxDebt - owed;
    }
}
