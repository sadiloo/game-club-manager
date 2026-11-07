using GameClub.Billing;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.AspNetCore.Mvc;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/operator/members")]
public sealed class OperatorMemberController(
    OperatorMemberWalletService wallet,
    OperatorMemberContext members,
    OperatorPackageService packages) : ControllerBase
{
    [HttpGet("customers/{customerId:guid}/wallet")]
    public async Task<IActionResult> GetWallet(Guid customerId, CancellationToken ct)
        => Ok(await wallet.GetWalletAsync(customerId, ct));

    public sealed record TopUpRequest(string Method, long AmountToman, string? DestinationCardId, string? Reference);

    [HttpPost("customers/{customerId:guid}/wallet/top-up")]
    public async Task<IActionResult> TopUp(Guid customerId, TopUpRequest body, CancellationToken ct)
    {
        await wallet.TopUpAsync(customerId, body.Method, body.AmountToman, body.DestinationCardId, body.Reference, ct);
        return Ok(await wallet.GetWalletAsync(customerId, ct));
    }

    [HttpPost("wallet/pending/{pendingId:long}/confirm")]
    public async Task<IActionResult> ConfirmTopUp(long pendingId, CancellationToken ct)
    {
        await wallet.ConfirmPendingTopUpAsync(pendingId, ct);
        return NoContent();
    }

    [HttpPost("wallet/pending/{pendingId:long}/reject")]
    public async Task<IActionResult> RejectTopUp(long pendingId, CancellationToken ct)
    {
        await wallet.RejectPendingTopUpAsync(pendingId, ct);
        return NoContent();
    }

    public sealed record GiftRequest(long GiftToman);

    [HttpPost("customers/{customerId:guid}/wallet/gift")]
    public async Task<IActionResult> Gift(Guid customerId, GiftRequest body, CancellationToken ct)
    {
        await wallet.IssueGiftAsync(customerId, body.GiftToman, ct);
        return Ok(await wallet.GetWalletAsync(customerId, ct));
    }

    [HttpPost("customers/{customerId:guid}/opening-balance")]
    public async Task<IActionResult> OpeningBalance(Guid customerId, OperatorMemberWalletService.OpeningBalanceRequest body, CancellationToken ct)
    {
        await wallet.RecordOpeningBalanceAsync(customerId, body, ct);
        return Ok(await wallet.GetWalletAsync(customerId, ct));
    }

    public sealed record DebtFloorRequest(long? FloorToman);

    [HttpPut("settings/debt-floor")]
    public async Task<IActionResult> DebtFloor(DebtFloorRequest body, CancellationToken ct)
    {
        await wallet.SetDebtFloorAsync(body.FloorToman, ct);
        return NoContent();
    }

    public sealed record LevelRow(string Name, long MinimumLifetimeHours, int DiscountPercent);

    [HttpPut("settings/level-ladder")]
    public async Task<IActionResult> LevelLadder(IReadOnlyList<LevelRow> body, CancellationToken ct)
    {
        await wallet.UpdateLadderAsync(body.Select(r => new CustomerLevel(r.Name, r.MinimumLifetimeHours, r.DiscountPercent)).ToList(), ct);
        return NoContent();
    }

    [HttpGet("settings/level-ladder")]
    public async Task<IActionResult> GetLevelLadder(CancellationToken ct)
    {
        var settings = await members.LoadSettingsAsync(ct);
        var ladder = MemberSettingsMapper.LoadLadder(settings);
        return Ok(ladder.Levels.Select(l => new { l.Name, l.MinimumLifetimeHours, l.DiscountPercent }));
    }

    public sealed record PackageSellRequest(string ProductId, IReadOnlyList<OperatorBillService.PaymentInput> Payments);

    [HttpGet("packages")]
    public async Task<IActionResult> ListPackages(CancellationToken ct)
        => Ok(await packages.ListProductsAsync(ct));

    public sealed record PackageProductBody(string Name, long PriceToman, long Minutes, int? ExpiresAfterDays, string? AllowedGroupIdsJson);

    [HttpPut("packages/{productId}")]
    public async Task<IActionResult> UpsertPackage(string productId, PackageProductBody body, CancellationToken ct)
    {
        await packages.UpsertProductAsync(productId, body.Name, body.PriceToman, body.Minutes, body.ExpiresAfterDays, body.AllowedGroupIdsJson, ct);
        return NoContent();
    }

    [HttpPost("customers/{customerId:guid}/packages/sell")]
    public async Task<IActionResult> SellPackage(Guid customerId, PackageSellRequest body, CancellationToken ct)
    {
        await packages.SellPackageAsync(customerId, body.ProductId, body.Payments, ct);
        return NoContent();
    }

    [HttpGet("wallet/pending")]
    public async Task<IActionResult> ListPendingWalletTopUps(CancellationToken ct)
        => Ok(await wallet.ListPendingTopUpsAsync(ct));

    [HttpGet("settings/birthday")]
    public async Task<IActionResult> GetBirthdaySettings(CancellationToken ct)
        => Ok(await wallet.GetBirthdaySettingsAsync(ct));

    public sealed record BirthdaySettingsBody(int Percent, string WindowKind, int WindowDays, string Anchor);

    [HttpPut("settings/birthday")]
    public async Task<IActionResult> BirthdaySettings(BirthdaySettingsBody body, CancellationToken ct)
    {
        await wallet.UpdateBirthdaySettingsAsync(body.Percent, body.WindowKind, body.WindowDays, body.Anchor, ct);
        return NoContent();
    }

    public sealed record CustomerBirthdayBody(int Year, int Month, int Day);

    [HttpPut("customers/{customerId:guid}/birthday")]
    public async Task<IActionResult> SetCustomerBirthday(Guid customerId, CustomerBirthdayBody body, CancellationToken ct)
    {
        await wallet.SetCustomerBirthdayAsync(customerId, body.Year, body.Month, body.Day, ct);
        return NoContent();
    }
}
