using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/operator/auth")]
public sealed class OperatorAuthController(OperatorAuthService auth) : ControllerBase
{
    public sealed record LoginRequest(string Username, string Pin);

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest body, CancellationToken ct)
    {
        var user = await auth.LoginAsync(body.Username, body.Pin, ct);
        var secret = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["OperatorSessionSecret"] ?? "dev-operator-secret";
        var token = OperatorAuthService.CreateSessionToken(user.Id, secret);
        Response.Cookies.Append(
            OperatorSessionMiddleware.CookieName,
            token,
            new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
        return Ok(new { user.Id, user.Username, role = user.Role.ToString() });
    }
}

[ApiController]
[Route("api/operator")]
public sealed class OperatorPanelController(
    OperatorGridService grid,
    OperatorBillService bills,
    OperatorInvoiceService invoices,
    ManualSettleQueueService manualSettleQueue,
    SessionService sessions,
    StationService stations,
    DestinationCardService destinationCards,
    OperatorUserService users,
    OperatorCatalogService catalog,
    OperatorBuffetSaleService buffetSales,
    OperatorCustomerService customers,
    OperatorPrepaidService prepaid,
    OperatorContext context) : ControllerBase
{
    [HttpGet("grid")]
    public async Task<IActionResult> Grid(CancellationToken ct) => Ok(await grid.GetGridAsync(ct));

    [HttpGet("customers/search")]
    public async Task<IActionResult> SearchCustomers([FromQuery] string? q, CancellationToken ct)
        => Ok(await customers.SearchAsync(q, ct));

    public sealed record CreateCustomerRequest(
        string Name,
        string Mobile,
        string? MembershipCode,
        Guid? LinkSessionId,
        Guid? LinkOpenSaleId);

    [HttpPost("customers")]
    public async Task<IActionResult> CreateCustomer(CreateCustomerRequest body, CancellationToken ct)
    {
        var created = await customers.CreateAsync(
            body.Name,
            body.Mobile,
            body.MembershipCode,
            body.LinkSessionId,
            body.LinkOpenSaleId,
            ct);
        return Created($"/api/operator/customers/{created.Id}", created);
    }

    public sealed record LinkCustomerRequest(Guid? CustomerId);

    [HttpPut("sessions/{sessionId:guid}/customer")]
    public async Task<IActionResult> LinkSessionCustomer(Guid sessionId, LinkCustomerRequest body, CancellationToken ct)
    {
        if (body.CustomerId is Guid customerId)
        {
            return Ok(await customers.LinkSessionAsync(sessionId, customerId, ct));
        }

        await customers.ClearSessionLinkAsync(sessionId, ct);
        return NoContent();
    }

    [HttpPut("buffet-only/{openSaleId:guid}/customer")]
    public async Task<IActionResult> LinkBuffetCustomer(Guid openSaleId, LinkCustomerRequest body, CancellationToken ct)
    {
        if (body.CustomerId is Guid customerId)
        {
            return Ok(await customers.LinkOpenSaleAsync(openSaleId, customerId, ct));
        }

        await customers.ClearOpenSaleLinkAsync(openSaleId, ct);
        return NoContent();
    }

    [HttpPut("customers/{customerId:guid}")]
    public IActionResult UpdateCustomer(Guid customerId)
        => StatusCode(StatusCodes.Status403Forbidden, new { code = "forbidden", message = "Customer profile edit is not available." });

    [HttpPost("stations/{stationId}/start")]
    public async Task<IActionResult> StartStation(string stationId, CancellationToken ct)
    {
        var station = await stations.GetAsync(stationId, ct);
        var session = await sessions.StartAsync(station.Id, station.DefaultPlayerCount, station.DefaultPlayMode, null, ct);
        return Created($"/api/sessions/{session.Id}", new { session.Id, session.StationId, session.Status });
    }

    [HttpPatch("stations/{stationId}/defaults")]
    public async Task<IActionResult> UpdateDefaults(string stationId, UpdateDefaultsRequest body, CancellationToken ct)
    {
        var db = HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var id = StationIdNormalizer.Normalize(stationId);
        var station = await db.Stations.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Station not found.");
        if (body.PlayerCount is int pc && pc is >= 1 and <= 4)
        {
            station.DefaultPlayerCount = pc;
        }

        if (body.Mode is not null && Enum.TryParse<GameClub.Billing.PlayMode>(body.Mode, true, out var mode))
        {
            station.DefaultPlayMode = mode;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { station.Id, station.DefaultPlayerCount, mode = station.DefaultPlayMode.ToString() });
    }

    public sealed record UpdateDefaultsRequest(int? PlayerCount, string? Mode);

    [HttpPost("sessions/{sessionId:guid}/bill/open")]
    public async Task<IActionResult> OpenBill(Guid sessionId, CancellationToken ct)
    {
        var bill = await bills.OpenConsoleBillAsync(sessionId, ct);
        return Ok(await BillResponseAsync(sessionId, bill, ct));
    }

    [HttpGet("sessions/{sessionId:guid}/bill")]
    public async Task<IActionResult> GetBill(Guid sessionId, CancellationToken ct)
        => Ok(await bills.GetBillDetailAsync(sessionId, ct));

    [HttpPost("sessions/{sessionId:guid}/bill/preview")]
    public async Task<IActionResult> PreviewBill(Guid sessionId, ConfirmRequest body, CancellationToken ct)
        => Ok(await bills.PreviewBillAsync(sessionId, body.Payments, ct));

    [HttpPost("sessions/{sessionId:guid}/pause")]
    public async Task<IActionResult> PauseSession(Guid sessionId, CancellationToken ct)
    {
        await sessions.PauseAsync(sessionId, ct);
        return NoContent();
    }

    [HttpPost("sessions/{sessionId:guid}/resume")]
    public async Task<IActionResult> ResumeSession(Guid sessionId, CancellationToken ct)
    {
        await sessions.ResumeAsync(sessionId, ct);
        return NoContent();
    }

    public sealed record PlayerCountBody(int PlayerCount);

    [HttpPatch("sessions/{sessionId:guid}/player-count")]
    public async Task<IActionResult> ChangePlayerCount(Guid sessionId, PlayerCountBody body, CancellationToken ct)
    {
        await sessions.ChangePlayerCountAsync(sessionId, body.PlayerCount, ct);
        return NoContent();
    }

    [HttpPost("sessions/{sessionId:guid}/bill/cancel")]
    public async Task<IActionResult> CancelBill(Guid sessionId, CancellationToken ct)
    {
        await bills.CancelConsoleBillAsync(sessionId, ct);
        return NoContent();
    }

    public sealed record SurchargeRequest(string? Reason);

    [HttpPost("sessions/{sessionId:guid}/bill/surcharge")]
    public async Task<IActionResult> Surcharge(Guid sessionId, SurchargeRequest body, CancellationToken ct)
    {
        await bills.AddSurchargeAsync(sessionId, body.Reason, ct);
        return Ok(await bills.GetBillDetailAsync(sessionId, ct));
    }

    public sealed record ConfirmRequest(IReadOnlyList<OperatorBillService.PaymentInput> Payments);

    public sealed record PrepaidDurationBody(int Minutes);

    [HttpPost("sessions/{sessionId:guid}/prepaid/duration")]
    public async Task<IActionResult> PrepaidDuration(Guid sessionId, PrepaidDurationBody body, CancellationToken ct)
        => Ok(await prepaid.StartDurationAsync(sessionId, body.Minutes, ct));

    public sealed record PrepaidAmountBody(long AmountToman);

    [HttpPost("sessions/{sessionId:guid}/prepaid/amount")]
    public async Task<IActionResult> PrepaidAmount(Guid sessionId, PrepaidAmountBody body, CancellationToken ct)
        => Ok(await prepaid.StartAmountAsync(sessionId, body.AmountToman, ct));

    [HttpGet("sessions/{sessionId:guid}/prepaid")]
    public async Task<IActionResult> PrepaidStatus(Guid sessionId, CancellationToken ct)
        => Ok(await prepaid.GetStatusAsync(sessionId, ct));

    [HttpPost("sessions/{sessionId:guid}/prepaid/dismiss-alert")]
    public async Task<IActionResult> DismissPrepaidAlert(Guid sessionId, CancellationToken ct)
    {
        await prepaid.MarkEndAlertSentAsync(sessionId, ct);
        return NoContent();
    }

    [HttpPost("sessions/{sessionId:guid}/bill/confirm")]
    public async Task<IActionResult> ConfirmBill(Guid sessionId, ConfirmRequest body, CancellationToken ct)
    {
        var invoice = await bills.ConfirmBillAsync(sessionId, body.Payments, ct);
        return Ok(new
        {
            invoice.Number,
            unroundedPayableToman = invoice.UnroundedPayableToman,
            roundingToman = invoice.RoundingToman,
            collectibleToman = invoice.CollectibleToman,
            surchargeToman = invoice.SurchargeToman,
            billedToUtc = invoice.BilledToUtc,
            status = invoice.Status,
        });
    }

    [HttpGet("manual-settle-queue")]
    public async Task<IActionResult> ManualSettleQueue(CancellationToken ct)
    {
        manualSettleQueue.EnsureMayResolve();
        return Ok(await manualSettleQueue.ListAsync(ct));
    }

    [HttpPost("sessions/{sessionId:guid}/manual-settle")]
    public async Task<IActionResult> ResolveManualSettle(Guid sessionId, SessionsController.ManualSettleRequest body, CancellationToken ct)
    {
        manualSettleQueue.EnsureMayResolve();
        var pending = await sessions.ResolveManualSettleAsync(sessionId, body.EndAt, ct);
        return Ok(await bills.GetBillDetailAsync(sessionId, ct));
    }

    [HttpGet("invoices/{number:long}")]
    public async Task<IActionResult> GetInvoice(long number, CancellationToken ct)
    {
        var invoice = await invoices.GetAsync(number, ct);
        return Ok(new
        {
            invoice.Number,
            customerId = invoice.CustomerId,
            linkedCustomer = invoice.Customer is null
                ? null
                : new
                {
                    invoice.Customer.Id,
                    invoice.Customer.Name,
                    invoice.Customer.Mobile,
                    membershipCode = invoice.Customer.MembershipCode,
                },
            invoice.TimeGrossToman,
            invoice.BuffetGrossToman,
            invoice.ServicesGrossToman,
            invoice.UnroundedPayableToman,
            invoice.RoundingToman,
            invoice.CollectibleToman,
            invoice.SurchargeToman,
            invoice.Status,
            invoice.VoidReason,
            invoice.BilledToUtc,
            readOnly = context.Role == UserRole.Operator,
            payments = invoice.PaymentLines.Select(p => new
            {
                p.Id,
                method = p.Method.ToString(),
                p.AmountToman,
                p.DestinationCardId,
                p.Reference,
                status = p.ConfirmationStatus.ToString(),
            }),
        });
    }

    [HttpPost("invoices/{number:long}/void")]
    public async Task<IActionResult> VoidInvoice(long number, VoidRequest body, CancellationToken ct)
    {
        await invoices.VoidAsync(number, body.Reason, ct);
        return NoContent();
    }

    public sealed record VoidRequest(string? Reason);

    [HttpPost("payments/{paymentLineId:long}/confirm")]
    public async Task<IActionResult> ConfirmPayment(long paymentLineId, CancellationToken ct)
    {
        await invoices.ConfirmCardToCardAsync(paymentLineId, ct);
        return NoContent();
    }

    [HttpPost("payments/{paymentLineId:long}/reject")]
    public async Task<IActionResult> RejectPayment(long paymentLineId, CancellationToken ct)
    {
        await invoices.RejectCardToCardAsync(paymentLineId, ct);
        return NoContent();
    }

    [HttpGet("payments/pending-card-to-card")]
    public async Task<IActionResult> PendingCardToCardPayments(CancellationToken ct)
        => Ok(await invoices.ListPendingCardToCardAsync(ct));

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog(CancellationToken ct) => Ok(await catalog.ListCatalogAsync(ct));

    [HttpGet("catalog/low-stock")]
    public async Task<IActionResult> LowStock(CancellationToken ct) => Ok(await catalog.LowStockAsync(ct));

    [HttpPut("catalog/items/{id}")]
    public async Task<IActionResult> PutCatalogItem(string id, PutCatalogItemRequest body, CancellationToken ct)
    {
        var item = await catalog.UpsertItemAsync(new OperatorCatalogService.CatalogItemInput(
            id,
            body.CategoryId,
            body.Name,
            body.Kind,
            body.UnitPriceToman,
            body.StockQuantity,
            body.LowStockWarningThreshold), ct);
        return Ok(new { item.Id, item.Name, kind = item.Kind.ToString(), item.UnitPriceToman, item.StockQuantity, item.LowStockWarningThreshold });
    }

    public sealed record PutCatalogItemRequest(
        string CategoryId,
        string Name,
        string Kind,
        long UnitPriceToman,
        int? StockQuantity,
        int? LowStockWarningThreshold);

    [HttpPost("catalog/items/{id}/stock-adjust")]
    public async Task<IActionResult> AdjustStock(string id, StockAdjustRequest body, CancellationToken ct)
    {
        await catalog.AdjustStockAsync(id, body.Delta, body.Reason, ct);
        return NoContent();
    }

    public sealed record StockAdjustRequest(int Delta, string? Reason);

    [HttpGet("sessions/{sessionId:guid}/sale")]
    public async Task<IActionResult> GetSessionSale(Guid sessionId, CancellationToken ct)
        => Ok(await buffetSales.GetSessionSaleAsync(sessionId, ct));

    public sealed record SaleLineRequest(string CatalogItemId, long? CustomAmountToman);

    [HttpPost("sessions/{sessionId:guid}/sale/lines")]
    public async Task<IActionResult> AddSessionSaleLine(Guid sessionId, SaleLineRequest body, CancellationToken ct)
        => Ok(await buffetSales.AddLineToSessionAsync(sessionId, body.CatalogItemId, body.CustomAmountToman, ct));

    [HttpDelete("sessions/{sessionId:guid}/sale/lines/{lineId:long}")]
    public async Task<IActionResult> RemoveSessionSaleLine(Guid sessionId, long lineId, CancellationToken ct)
        => Ok(await buffetSales.RemoveLineFromSessionAsync(sessionId, lineId, ct));

    [HttpPost("buffet-only/start")]
    public async Task<IActionResult> StartBuffetOnly(CancellationToken ct)
    {
        var sale = await buffetSales.StartBuffetOnlyAsync(ct);
        return Ok(new { openSaleId = sale.Id });
    }

    [HttpGet("buffet-only/{openSaleId:guid}")]
    public async Task<IActionResult> GetBuffetOnly(Guid openSaleId, CancellationToken ct)
        => Ok(await buffetSales.GetBuffetOnlyBillAsync(openSaleId, ct));

    [HttpPost("buffet-only/{openSaleId:guid}/lines")]
    public async Task<IActionResult> AddBuffetOnlyLine(Guid openSaleId, SaleLineRequest body, CancellationToken ct)
        => Ok(await buffetSales.AddLineToOpenSaleAsync(openSaleId, body.CatalogItemId, body.CustomAmountToman, ct));

    [HttpDelete("buffet-only/{openSaleId:guid}/lines/{lineId:long}")]
    public async Task<IActionResult> RemoveBuffetOnlyLine(Guid openSaleId, long lineId, CancellationToken ct)
        => Ok(await buffetSales.RemoveLineFromOpenSaleAsync(openSaleId, lineId, ct));

    [HttpPost("buffet-only/{openSaleId:guid}/bill/preview")]
    public async Task<IActionResult> PreviewBuffetOnlyBill(Guid openSaleId, ConfirmRequest body, CancellationToken ct)
        => Ok(await buffetSales.PreviewBuffetOnlyBillAsync(openSaleId, body.Payments, ct));

    [HttpPost("buffet-only/{openSaleId:guid}/checkout")]
    public async Task<IActionResult> CheckoutBuffetOnly(Guid openSaleId, CancellationToken ct)
    {
        var bill = await buffetSales.CheckoutBuffetOnlyAsync(openSaleId, ct);
        return Ok(new
        {
            openSaleId,
            unroundedPayableToman = bill.UnroundedPayableToman,
            roundingToman = bill.RoundingToman,
            collectibleToman = bill.CollectibleToman,
            timeGrossToman = bill.TimeGrossToman,
            buffetGrossToman = bill.BuffetGrossToman,
            servicesGrossToman = bill.ServicesGrossToman,
        });
    }

    [HttpPost("buffet-only/{openSaleId:guid}/confirm")]
    public async Task<IActionResult> ConfirmBuffetOnly(Guid openSaleId, ConfirmRequest body, CancellationToken ct)
    {
        var invoice = await buffetSales.ConfirmBuffetOnlyAsync(openSaleId, body.Payments, ct);
        return Ok(new
        {
            invoice.Number,
            invoice.TimeGrossToman,
            invoice.BuffetGrossToman,
            invoice.ServicesGrossToman,
            unroundedPayableToman = invoice.UnroundedPayableToman,
            roundingToman = invoice.RoundingToman,
            collectibleToman = invoice.CollectibleToman,
            status = invoice.Status,
        });
    }

    [HttpGet("destination-cards")]
    public async Task<IActionResult> DestinationCards(CancellationToken ct) => Ok(await destinationCards.ListAsync(ct));

    [HttpPut("destination-cards/{id}")]
    public async Task<IActionResult> PutDestinationCard(string id, PutCardRequest body, CancellationToken ct)
    {
        var card = await destinationCards.UpsertAsync(id, body.Label, ct);
        return Ok(new { card.Id, card.Label });
    }

    public sealed record PutCardRequest(string Label);

    [HttpGet("users")]
    public async Task<IActionResult> ListUsers(CancellationToken ct)
        => Ok((await users.ListAsync(ct)).Select(u => new { u.Id, u.Username, role = u.Role.ToString() }));

    public sealed record CreateUserRequest(string Username, string Pin, string Role);

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser(CreateUserRequest body, CancellationToken ct)
    {
        var role = Enum.Parse<UserRole>(body.Role, true);
        var user = await users.CreateAsync(body.Username, body.Pin, role, ct);
        return Created($"/api/operator/users/{user.Id}", new { user.Id, user.Username, role = user.Role.ToString() });
    }

    private async Task<object> BillResponseAsync(Guid sessionId, PendingBillEntity bill, CancellationToken ct)
    {
        try
        {
            return await bills.GetBillDetailAsync(sessionId, ct);
        }
        catch (ApiException ex) when (ex.Code == "no_bill")
        {
            return BillDto(bill);
        }
    }

    private static object BillDto(PendingBillEntity bill) => new
    {
        bill.SessionId,
        unroundedPayableToman = bill.UnroundedPayableToman,
        roundingToman = bill.RoundingToman,
        collectibleToman = bill.CollectibleToman,
        surchargeToman = bill.SurchargeToman,
        frozenAtUtc = bill.FrozenAtUtc,
        source = bill.Source,
        stopReason = bill.StopReason,
    };
}
