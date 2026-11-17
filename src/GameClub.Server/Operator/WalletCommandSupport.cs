using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public static class WalletCommandTypes
{
    public const string TopUp = "topup";
    public const string TopUpPending = "topup_pending";
    public const string Correction = "correction";
}

public static class WalletCommandSupport
{
    public static void EnsureCommandIdPresent(Guid? commandId)
    {
        if (commandId is not Guid id || id == Guid.Empty)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "command_id_required", "commandId is required.");
        }
    }

    public static string BuildTopUpFingerprint(Guid customerId, string method, long amountToman, long giftToman)
    {
        return Sha256Hex(
            $$"""{"amountToman":{{amountToman}},"customerId":"{{customerId:D}}","giftToman":{{giftToman}},"method":{{JsonSerializer.Serialize(method)}}}""");
    }

    public static string BuildCorrectionFingerprint(Guid topUpGroupId, long newAmountToman, long newGiftToman)
    {
        return Sha256Hex(
            $$"""{"newAmountToman":{{newAmountToman}},"newGiftToman":{{newGiftToman}},"topUpGroupId":"{{topUpGroupId:D}}"}""");
    }

    public static async Task<object?> TryReplayAsync(AppDbContext db, Guid commandId, CancellationToken ct)
    {
        var row = await db.WalletCommands.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CommandId == commandId, ct);
        if (row?.ResultJson is not { } json)
        {
            return null;
        }

        return JsonSerializer.Deserialize<object>(json)
            ?? throw new InvalidOperationException("Corrupt wallet command result.");
    }

    public static async Task<object?> TryReplayOrThrowReuseAsync(
        AppDbContext db,
        Guid commandId,
        string fingerprint,
        CancellationToken ct)
    {
        var row = await db.WalletCommands.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CommandId == commandId, ct);
        if (row is null)
        {
            return null;
        }

        if (!string.Equals(row.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "command_id_reused",
                "commandId was already used with a different request.");
        }

        if (row.ResultJson is not { } json)
        {
            return null;
        }

        return JsonSerializer.Deserialize<object>(json)
            ?? throw new InvalidOperationException("Corrupt wallet command result.");
    }

    public static void RecordWalletCommand(
        AppDbContext db,
        IClock clock,
        Guid commandId,
        string commandType,
        Guid customerId,
        string fingerprint,
        string resultJson,
        Guid? topUpGroupId = null,
        long? pendingWalletTopUpId = null)
    {
        db.WalletCommands.Add(new WalletCommandEntity
        {
            CommandId = commandId,
            CommandType = commandType,
            CustomerId = customerId,
            RequestFingerprint = fingerprint,
            TopUpGroupId = topUpGroupId,
            PendingWalletTopUpId = pendingWalletTopUpId,
            ResultJson = resultJson,
            AppliedAtUtc = clock.UtcNow.UtcDateTime,
        });
    }

    public static async Task<object> HandleUniqueViolationAsync(
        AppDbContext db,
        Guid commandId,
        string fingerprint,
        CancellationToken ct)
    {
        var replay = await TryReplayOrThrowReuseAsync(db, commandId, fingerprint, ct);
        if (replay is not null)
        {
            return replay;
        }

        throw new InvalidOperationException("Wallet command unique violation without stored result.");
    }

    private static string Sha256Hex(string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}