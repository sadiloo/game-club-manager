namespace GameClub.Billing;

public enum PaymentMethod
{
    Cash = 0,
    CardReader = 1,
    CardToCard = 2,
    Wallet = 3,
}

/// <summary>
/// Gift credit is spent before real (paid) credit by default, so a member keeps
/// the balance they can still cash out or move. Switch to <see cref="WalletDrawOrder.RealThenGift"/>
/// when the branch wants gift credit held back.
/// </summary>
public enum WalletDrawOrder
{
    GiftThenReal = 0,
    RealThenGift = 1,
}

public sealed record MemberWallet(string MemberId, long RealCreditToman, long GiftCreditToman);

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
        WalletDrawOrder walletOrder = WalletDrawOrder.GiftThenReal)
    {
        if (payableToman < 0)
        {
            throw new BillingException("invalid_money", "Payable total cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(tenders);
        var errors = new List<PaymentError>();
        var balances = new Dictionary<string, (long Real, long Gift)>(StringComparer.Ordinal);
        foreach (var wallet in wallets ?? [])
        {
            if (string.IsNullOrWhiteSpace(wallet.MemberId))
            {
                throw new BillingException("invalid_payment", "Wallet member id is required.");
            }

            if (wallet.RealCreditToman < 0 || wallet.GiftCreditToman < 0)
            {
                throw new BillingException("invalid_money", $"Wallet '{wallet.MemberId}' has a negative balance.");
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

            if (!TryDraw(ref balance, tender.AmountToman, walletOrder, out var giftDrawn, out var realDrawn))
            {
                errors.Add(new PaymentError(
                    PaymentErrors.InsufficientWallet,
                    $"Wallet '{tender.MemberId}' cannot cover {tender.AmountToman} toman."));
                results.Add(new TenderResult(i, tender.Method, tender.AmountToman, tender.MemberId, 0, 0, false));
                continue;
            }

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

    private static bool TryDraw(
        ref (long Real, long Gift) balance,
        long amount,
        WalletDrawOrder order,
        out long giftDrawn,
        out long realDrawn)
    {
        if (amount > balance.Real + balance.Gift)
        {
            giftDrawn = 0;
            realDrawn = 0;
            return false;
        }

        if (order == WalletDrawOrder.GiftThenReal)
        {
            giftDrawn = Math.Min(balance.Gift, amount);
            realDrawn = amount - giftDrawn;
        }
        else
        {
            realDrawn = Math.Min(balance.Real, amount);
            giftDrawn = amount - realDrawn;
        }

        balance = (balance.Real - realDrawn, balance.Gift - giftDrawn);
        return true;
    }
}
