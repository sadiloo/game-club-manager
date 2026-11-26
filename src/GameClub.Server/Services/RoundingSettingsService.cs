using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Services;

public sealed class RoundingSettingsService(AppDbContext db, AuditService audit, OperatorContext op)
{
    public async Task<RoundingSettingsResponse> GetAsync(CancellationToken ct = default)
    {
        var row = await db.BranchRounding.AsNoTracking().SingleAsync(ct);
        return ToResponse(row);
    }

    public async Task<RoundingSettingsResponse> UpdateAsync(RoundingSettingsUpdateRequest request, CancellationToken ct = default)
    {
        if (op.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "تنظیمات کافه فقط برای مدیر است.");
        }

        if (request.StepToman <= 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rounding", "گام گرد کردن باید بیشتر از صفر باشد.");
        }

        if (!request.Direction.Equals("Up", StringComparison.OrdinalIgnoreCase)
            && !request.Direction.Equals("Down", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rounding", "جهت گرد کردن باید بالا یا پایین باشد.");
        }

        if (request.WalletMinBillToman < 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rounding", "حداقل مبلغ رند کیف پول نامعتبر است.");
        }

        var row = await db.BranchRounding.SingleAsync(ct);
        var before = ToResponse(row);
        row.RoundingEnabled = request.Enabled;
        row.StepToman = request.StepToman;
        row.Direction = request.Direction;
        row.ApplyToWallet = request.ApplyToWallet;
        row.WalletMinBillToman = request.WalletMinBillToman;
        row.AllowWalletShortBalanceDebt = request.AllowWalletShortBalanceDebt;
        await audit.AppendAsync("rounding.settings", new { before, after = ToResponse(row) }, ct);
        await db.SaveChangesAsync(ct);
        return ToResponse(row);
    }

    public async Task<(RoundingPolicy Policy, PaymentRoundingSettings Settings)> LoadForPaymentAsync(CancellationToken ct = default)
        => RoundingSettingsResolver.ResolveRoundingSettingsForBill(await db.BranchRounding.AsNoTracking().SingleAsync(ct));

    public async Task<BranchRatesSnapshot> ResolveRatesForPaymentAsync(BranchRatesSnapshot ratesSnapshot, CancellationToken ct = default)
        => RoundingSettingsResolver.ResolveRatesForPayment(
            ratesSnapshot,
            await db.BranchRounding.AsNoTracking().SingleAsync(ct));

    internal static RoundingPolicy BuildRoundingPolicy(BranchRoundingEntity row)
    {
        if (!row.RoundingEnabled)
        {
            return RoundingPolicy.Off;
        }

        var direction = row.Direction.Equals("Down", StringComparison.OrdinalIgnoreCase)
            ? RoundingDirection.Down
            : RoundingDirection.Up;
        return RoundingPolicy.Create(row.StepToman, direction, RoundingScope.WholeInvoice);
    }

    internal static PaymentRoundingSettings BuildPaymentSettings(BranchRoundingEntity row)
        => new()
        {
            Enabled = row.RoundingEnabled,
            ApplyToWallet = row.ApplyToWallet,
            WalletMinBillToman = row.WalletMinBillToman,
            AllowWalletShortBalanceDebt = row.AllowWalletShortBalanceDebt,
        };

    private static RoundingSettingsResponse ToResponse(BranchRoundingEntity row)
        => new(
            row.RoundingEnabled,
            row.StepToman,
            row.Direction,
            row.ApplyToWallet,
            row.WalletMinBillToman,
            row.AllowWalletShortBalanceDebt);
}

public static class RoundingSettingsResolver
{
    public static (RoundingPolicy Policy, PaymentRoundingSettings Settings) ResolveRoundingSettingsForBill(BranchRoundingEntity row)
        => (RoundingSettingsService.BuildRoundingPolicy(row), RoundingSettingsService.BuildPaymentSettings(row));

    public static BranchRatesSnapshot ResolveRatesForPayment(BranchRatesSnapshot ratesSnapshot, BranchRoundingEntity liveRow)
    {
        var (policy, _) = ResolveRoundingSettingsForBill(liveRow);
        return ratesSnapshot with { Rounding = policy };
    }

}

public sealed record RoundingSettingsResponse(
    bool Enabled,
    long StepToman,
    string Direction,
    bool ApplyToWallet,
    long WalletMinBillToman,
    bool AllowWalletShortBalanceDebt);

public sealed record RoundingSettingsUpdateRequest(
    bool Enabled,
    long StepToman,
    string Direction,
    bool ApplyToWallet,
    long WalletMinBillToman,
    bool AllowWalletShortBalanceDebt);