using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public sealed record CustomerDto(Guid Id, string Name, string Mobile, string? MembershipCode);

public static class CustomerMapper
{
    public static CustomerDto ToDto(CustomerEntity entity)
        => new(entity.Id, entity.Name, entity.Mobile, entity.MembershipCode);

    public static object? ToLinkedChip(CustomerEntity? entity)
        => entity is null ? null : new { entity.Id, entity.Name, entity.Mobile, membershipCode = entity.MembershipCode };
}

public sealed class OperatorCustomerService(AppDbContext db, IClock clock, AuditService audit)
{
    public async Task<IReadOnlyList<CustomerDto>> SearchAsync(string? query, CancellationToken ct)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        var mobileNeedle = CustomerIdentityNormalizer.NormalizeMobile(trimmed);
        var codeKey = CustomerIdentityNormalizer.MembershipCodeKey(trimmed);
        var like = $"%{EscapeLike(trimmed)}%";

        var rows = await db.Customers.AsNoTracking()
            .Where(c =>
                c.Mobile == mobileNeedle
                || c.Mobile.StartsWith(mobileNeedle)
                || EF.Functions.ILike(c.Name, like)
                || (c.MembershipCodeKey != null && (c.MembershipCodeKey == codeKey || EF.Functions.ILike(c.MembershipCode!, like))))
            .OrderBy(c => c.Name)
            .Take(20)
            .ToListAsync(ct);
        return rows.Select(CustomerMapper.ToDto).ToList();
    }

    public async Task<CustomerDto> CreateAsync(
        string name,
        string mobile,
        string? membershipCode,
        Guid? linkSessionId,
        Guid? linkOpenSaleId,
        CancellationToken ct)
    {
        var customer = await CreateCoreAsync(name, mobile, membershipCode, ct);
        if (linkSessionId is Guid sessionId)
        {
            await SetSessionLinkCoreAsync(sessionId, customer.Id, ct);
        }
        else if (linkOpenSaleId is Guid openSaleId)
        {
            await SetOpenSaleLinkCoreAsync(openSaleId, customer.Id, ct);
        }

        return CustomerMapper.ToDto(customer);
    }

    public async Task<CustomerDto> LinkSessionAsync(Guid sessionId, Guid customerId, CancellationToken ct)
    {
        await EnsureCustomerExistsAsync(customerId, ct);
        await SetSessionLinkCoreAsync(sessionId, customerId, ct);
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId, ct);
        return CustomerMapper.ToDto(customer);
    }

    public async Task ClearSessionLinkAsync(Guid sessionId, CancellationToken ct)
        => await SetSessionLinkCoreAsync(sessionId, null, ct);

    public async Task<CustomerDto> LinkOpenSaleAsync(Guid openSaleId, Guid customerId, CancellationToken ct)
    {
        await EnsureCustomerExistsAsync(customerId, ct);
        await SetOpenSaleLinkCoreAsync(openSaleId, customerId, ct);
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == customerId, ct);
        return CustomerMapper.ToDto(customer);
    }

    public async Task ClearOpenSaleLinkAsync(Guid openSaleId, CancellationToken ct)
        => await SetOpenSaleLinkCoreAsync(openSaleId, null, ct);

    private async Task<CustomerEntity> CreateCoreAsync(string name, string mobile, string? membershipCode, CancellationToken ct)
    {
        var displayName = CustomerIdentityNormalizer.NormalizeName(name);
        if (displayName.Length == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_name", "Name is required.");
        }

        var normalizedMobile = CustomerIdentityNormalizer.NormalizeMobile(mobile);
        if (normalizedMobile.Length == 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_mobile", "Mobile is required.");
        }

        if (await db.Customers.AnyAsync(c => c.Mobile == normalizedMobile, ct))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "duplicate_mobile", "A customer with this mobile already exists.");
        }

        var code = CustomerIdentityNormalizer.NormalizeMembershipCode(membershipCode);
        var codeKey = CustomerIdentityNormalizer.MembershipCodeKey(code);
        if (codeKey is not null && await db.Customers.AnyAsync(c => c.MembershipCodeKey == codeKey, ct))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "duplicate_membership_code", "Membership code is already in use.");
        }

        var entity = new CustomerEntity
        {
            Id = Guid.NewGuid(),
            Name = displayName,
            Mobile = normalizedMobile,
            MembershipCode = code,
            MembershipCodeKey = codeKey,
            CreatedAtUtc = clock.UtcNow.UtcDateTime,
        };
        db.Customers.Add(entity);
        try
        {
            await audit.AppendAsync("customer.create", new { customerId = entity.Id, entity.Name, entity.Mobile, entity.MembershipCode }, ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsCustomerMobileUniqueViolation(ex))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "duplicate_mobile", "A customer with this mobile already exists.");
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsCustomerMembershipCodeUniqueViolation(ex))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "duplicate_membership_code", "Membership code is already in use.");
        }

        return entity;
    }

    private async Task SetSessionLinkCoreAsync(Guid sessionId, Guid? customerId, CancellationToken ct)
    {
        var session = await db.Sessions
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Session not found.");
        if (session.Status == SessionStatus.Closed)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "closed", "Session is closed.");
        }

        if (customerId is Guid cid
            && session.Station?.Group?.Kind == StationKind.Pc
            && await db.Sessions.AnyAsync(
                s => s.Id != sessionId
                    && s.CustomerId == cid
                    && s.Station!.Group!.Kind == StationKind.Pc
                    && (s.Status == SessionStatus.Open || s.Status == SessionStatus.Paused),
                ct))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "session_elsewhere", "Customer already has an open PC session.");
        }

        var before = session.CustomerId;
        session.CustomerId = customerId;
        await audit.AppendAsync(
            customerId is null ? "customer.unlink" : "customer.link",
            new
            {
                target = "session",
                sessionId,
                before,
                after = customerId,
            },
            ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task SetOpenSaleLinkCoreAsync(Guid openSaleId, Guid? customerId, CancellationToken ct)
    {
        var sale = await db.OpenSales.FirstOrDefaultAsync(o => o.Id == openSaleId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Open sale not found.");
        var before = sale.CustomerId;
        sale.CustomerId = customerId;
        await audit.AppendAsync(
            customerId is null ? "customer.unlink" : "customer.link",
            new
            {
                target = sale.IsBuffetOnly ? "buffet_only" : "session_sale",
                openSaleId,
                sessionId = sale.SessionId,
                before,
                after = customerId,
            },
            ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureCustomerExistsAsync(Guid customerId, CancellationToken ct)
    {
        if (!await db.Customers.AnyAsync(c => c.Id == customerId, ct))
        {
            throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Customer not found.");
        }
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
