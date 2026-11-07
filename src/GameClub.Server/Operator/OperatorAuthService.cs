using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Services;
using GameClub.Server.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public sealed class OperatorAuthService(AppDbContext db, OperatorContext context)
{
    private static readonly PasswordHasher<OperatorUserEntity> Hasher = new();

    public async Task<OperatorUserEntity> LoginAsync(string username, string pin, CancellationToken ct)
    {
        var user = await db.OperatorUsers.FirstOrDefaultAsync(u => u.Username == username && u.IsActive, ct)
            ?? throw new Services.ApiException(StatusCodes.Status401Unauthorized, "invalid_login", "Invalid username or PIN.");
        var result = Hasher.VerifyHashedPassword(user, user.PinHash, pin);
        if (result == PasswordVerificationResult.Failed)
        {
            throw new Services.ApiException(StatusCodes.Status401Unauthorized, "invalid_login", "Invalid username or PIN.");
        }

        context.UserId = user.Id;
        context.Role = user.Role;
        return user;
    }

    public static string HashPin(OperatorUserEntity user, string pin) => Hasher.HashPassword(user, pin);

    public static string CreateSessionToken(Guid userId, string secret)
    {
        var payload = userId.ToString("N");
        var sig = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));
        return $"{payload}.{sig}";
    }

    public static Guid? ParseSessionToken(string? token, string secret)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 2)
        {
            return null;
        }

        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(parts[0])));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[1])))
        {
            return null;
        }

        return Guid.TryParseExact(parts[0], "N", out var id) ? id : null;
    }
}

public sealed class OperatorUserService(AppDbContext db, AuditService audit, OperatorContext context)
{
    public async Task<IReadOnlyList<OperatorUserEntity>> ListAsync(CancellationToken ct)
        => await db.OperatorUsers.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);

    public async Task<OperatorUserEntity> CreateAsync(string username, string pin, UserRole role, CancellationToken ct)
    {
        RequireManager();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(pin))
        {
            throw new Services.ApiException(StatusCodes.Status400BadRequest, "invalid_user", "Username and PIN are required.");
        }

        var user = new OperatorUserEntity { Id = Guid.NewGuid(), Username = username.Trim(), Role = role };
        user.PinHash = OperatorAuthService.HashPin(user, pin);
        db.OperatorUsers.Add(user);
        await audit.AppendAsync("operator.user_create", new { userId = user.Id, username, role = role.ToString() }, ct);
        await db.SaveChangesAsync(ct);
        return user;
    }

    private void RequireManager()
    {
        if (context.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new Services.ApiException(StatusCodes.Status403Forbidden, "forbidden", "Manager role required.");
        }
    }
}

public sealed class DestinationCardService(AppDbContext db, AuditService audit, OperatorContext context)
{
    public async Task<IReadOnlyList<DestinationCardEntity>> ListAsync(CancellationToken ct)
        => await db.DestinationCards.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Label).ToListAsync(ct);

    public async Task<DestinationCardEntity> UpsertAsync(string id, string label, CancellationToken ct)
    {
        RequireManager();
        var card = await db.DestinationCards.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? new DestinationCardEntity { Id = id };
        card.Label = label;
        card.IsActive = true;
        if (db.Entry(card).State == EntityState.Detached)
        {
            db.DestinationCards.Add(card);
        }

        await audit.AppendAsync("settings.destination_card", new { id, label }, ct);
        await db.SaveChangesAsync(ct);
        return card;
    }

    private void RequireManager()
    {
        if (context.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new Services.ApiException(StatusCodes.Status403Forbidden, "forbidden", "Manager role required.");
        }
    }
}
