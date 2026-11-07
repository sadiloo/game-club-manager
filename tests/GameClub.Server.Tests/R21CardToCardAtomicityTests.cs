using System.Net;
using System.Net.Http.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

/// R3-1: status change, rejected row, and audit must commit atomically.
[Collection("server")]
public sealed class R21CardToCardAtomicityTests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    private async Task<long> CreatePendingCardToCardLineAsync(HttpClient op, string stationId)
    {
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync("/api/operator/destination-cards/card-a", new { label = "A" });
        await S3TestHelpers.RegisterConsole(_api, op, stationId, 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, stationId);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "CardToCard", amountToman = 110_000L, destinationCardId = "card-a" },
                },
            })).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.PaymentLines.Where(p => p.Invoice!.SessionId == sessionId).Select(p => p.Id).SingleAsync();
    }

    private static void InstallAuditInsertFault(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            create or replace function r21_fail_audit() returns trigger language plpgsql as $$
            begin
                if NEW."Action" like 'payment.card_to_card_%' then
                    raise exception 'r21 injected audit fault';
                end if;
                return NEW;
            end $$;
            drop trigger if exists r21_fail_audit on audit_log;
            create trigger r21_fail_audit before insert on audit_log for each row execute function r21_fail_audit();
            """);
    }

    private static void RemoveAuditInsertFault(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            drop trigger if exists r21_fail_audit on audit_log;
            drop function if exists r21_fail_audit();
            """);
    }

    private static void InstallSlowRejectedInsert(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            create or replace function r21_slow_rejected() returns trigger language plpgsql as $$
            begin
                perform pg_sleep(1);
                return NEW;
            end $$;
            drop trigger if exists r21_slow_rejected on rejected_card_to_card;
            create trigger r21_slow_rejected before insert on rejected_card_to_card for each row execute function r21_slow_rejected();
            """);
    }

    private static void RemoveSlowRejectedInsert(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            drop trigger if exists r21_slow_rejected on rejected_card_to_card;
            drop function if exists r21_slow_rejected();
            """);
    }

    [Fact]
    public async Task audit_insert_fault_rolls_back_status_so_retry_succeeds()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var rejectLineId = await CreatePendingCardToCardLineAsync(op, "PS5-R21-FR");
        var confirmLineId = await CreatePendingCardToCardLineAsync(op, "PS5-R21-FC");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            InstallAuditInsertFault(db);
        }

        var rejectStatus = (HttpStatusCode)(int)(await op.PostAsync($"/api/operator/payments/{rejectLineId}/reject", null)).StatusCode;
        var confirmStatus = (HttpStatusCode)(int)(await op.PostAsync($"/api/operator/payments/{confirmLineId}/confirm", null)).StatusCode;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            RemoveAuditInsertFault(db);

            Assert.Equal(HttpStatusCode.InternalServerError, rejectStatus);
            Assert.Equal(HttpStatusCode.InternalServerError, confirmStatus);
            Assert.Equal(PaymentConfirmationStatus.Pending, db.PaymentLines.Single(p => p.Id == rejectLineId).ConfirmationStatus);
            Assert.Equal(PaymentConfirmationStatus.Pending, db.PaymentLines.Single(p => p.Id == confirmLineId).ConfirmationStatus);
            Assert.Empty(db.RejectedCardToCard.Where(r => r.PaymentLineId == rejectLineId));
            Assert.Equal(0, db.AuditLog.Count(a => a.DetailsJson.Contains($"\"paymentLineId\":{rejectLineId}")));
            Assert.Equal(0, db.AuditLog.Count(a => a.DetailsJson.Contains($"\"paymentLineId\":{confirmLineId}")));
        }

        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/operator/payments/{rejectLineId}/reject", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/operator/payments/{confirmLineId}/confirm", null)).StatusCode);
    }

    [Fact]
    public async Task client_abort_during_reject_rolls_back_to_pending()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var lineId = await CreatePendingCardToCardLineAsync(op, "PS5-R21-AB");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            InstallSlowRejectedInsert(db);
        }

        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await op.PostAsync($"/api/operator/payments/{lineId}/reject", null, cts.Token));

        await Task.Delay(2000);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            RemoveSlowRejectedInsert(db);

            Assert.Equal(PaymentConfirmationStatus.Pending, db.PaymentLines.Single(p => p.Id == lineId).ConfirmationStatus);
            Assert.Empty(db.RejectedCardToCard.Where(r => r.PaymentLineId == lineId));
            Assert.Equal(0, db.AuditLog.Count(a => a.DetailsJson.Contains($"\"paymentLineId\":{lineId}")));
        }

        var pendingJson = await (await op.GetAsync("/api/operator/payments/pending-card-to-card")).Content.ReadAsStringAsync();
        Assert.Contains(lineId.ToString(), pendingJson);
        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/operator/payments/{lineId}/reject", null)).StatusCode);
    }
}
