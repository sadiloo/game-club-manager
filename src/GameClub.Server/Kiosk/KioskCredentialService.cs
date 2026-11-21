using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Kiosk;

public sealed class KioskCredentialService(AppDbContext db, IClock clock, AuditService audit, OperatorContext op)
{
    private static readonly PasswordHasher<KioskCredentialEntity> Hasher = new();

    public const string ManagerRole = "manager";
    public const string OperatorRole = "operator";

    public async Task<int> GetCredentialsVersionAsync(CancellationToken ct)
        => await db.AuditLog.CountAsync(a => a.Action == "kiosk.credential_set", ct);

    public async Task<KioskCredentialStatus> GetStatusAsync(CancellationToken ct)
    {
        var rows = await db.KioskCredentials.AsNoTracking().ToListAsync(ct);
        var manager = rows.Find(r => r.Role == ManagerRole);
        var kioskOperator = rows.Find(r => r.Role == OperatorRole);
        return new KioskCredentialStatus(
            manager is not null,
            kioskOperator is not null,
            manager?.UpdatedAtUtc,
            kioskOperator?.UpdatedAtUtc,
            await GetCredentialsVersionAsync(ct));
    }

    public async Task SetCredentialAsync(string role, string newPassword, CancellationToken ct)
    {
        RequireManagerOrOwner();
        ValidatePasswordFormat(newPassword);
        var normalized = role.Trim().ToLowerInvariant();
        if (normalized is not (ManagerRole or OperatorRole))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_role", "نقش نامعتبر است.");
        }

        var otherRole = normalized == ManagerRole ? OperatorRole : ManagerRole;
        var other = await db.KioskCredentials.AsNoTracking().FirstOrDefaultAsync(r => r.Role == otherRole, ct);
        if (other is not null && PasswordMatches(other, newPassword))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "kiosk_passwords_must_differ",
                "رمز مدیر کیوسک و رمز اپراتور کیوسک باید فرق داشته باشند.");
        }

        var entity = await db.KioskCredentials.FirstOrDefaultAsync(r => r.Role == normalized, ct);
        if (entity is null)
        {
            entity = new KioskCredentialEntity { Role = normalized };
            db.KioskCredentials.Add(entity);
        }

        entity.PasswordHash = HashPassword(entity, newPassword);
        entity.UpdatedAtUtc = clock.UtcNow.UtcDateTime;
        entity.UpdatedByUserId = op.UserId;
        await audit.AppendAsync("kiosk.credential_set", new { role = normalized }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<(string ManagerHash, string OperatorHash)?> TryGetHashesAsync(CancellationToken ct)
    {
        var rows = await db.KioskCredentials.AsNoTracking().ToListAsync(ct);
        var manager = rows.Find(r => r.Role == ManagerRole);
        var kioskOperator = rows.Find(r => r.Role == OperatorRole);
        if (manager is null || kioskOperator is null)
        {
            return null;
        }

        return (manager.PasswordHash, kioskOperator.PasswordHash);
    }

    public async Task<bool> VerifyManagerPasswordAsync(string password, CancellationToken ct)
    {
        var row = await db.KioskCredentials.AsNoTracking().FirstOrDefaultAsync(r => r.Role == ManagerRole, ct);
        if (row is null)
        {
            return false;
        }

        return PasswordMatches(row, password);
    }

    private void RequireManagerOrOwner()
    {
        if (op.Role is not (UserRole.Manager or UserRole.Owner))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "فقط مدیر یا مالک مجاز است.");
        }
    }

    public static void ValidatePasswordFormat(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 6)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_password", "رمز باید دست‌کم ۶ نویسه باشد.");
        }

        foreach (var ch in password)
        {
            if (!(char.IsAsciiLetterOrDigit(ch)))
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "invalid_password",
                    "رمز فقط حرف لاتین و رقم می‌پذیرد.");
            }
        }
    }

    private static string HashPassword(KioskCredentialEntity row, string password)
        => Hasher.HashPassword(row, password);

    private static bool PasswordMatches(KioskCredentialEntity row, string password)
    {
        var result = Hasher.VerifyHashedPassword(row, row.PasswordHash, password);
        return result is not PasswordVerificationResult.Failed;
    }
}

public sealed record KioskCredentialStatus(
    bool ManagerSet,
    bool OperatorSet,
    DateTime? ManagerUpdatedAtUtc,
    DateTime? OperatorUpdatedAtUtc,
    int CredentialsVersion);