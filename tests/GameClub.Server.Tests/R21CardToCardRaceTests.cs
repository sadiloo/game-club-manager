using System.Net;
using System.Net.Http.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

/// R2-1: card-to-card confirm/reject must not double-apply under concurrent requests.
[Collection("server")]
public sealed class R21CardToCardRaceTests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    private static async Task<HttpResponseMessage[]> RaceAsync(params Func<Task<HttpResponseMessage>>[] actions)
    {
        using var gate = new ManualResetEventSlim(false);
        var tasks = actions.Select(a => Task.Run(async () =>
        {
            gate.Wait();
            return await a();
        })).ToArray();
        gate.Set();
        return await Task.WhenAll(tasks);
    }

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

    private void AssertRoundOutcome(
        long paymentLineId,
        HttpResponseMessage[] responses,
        int auditDelta,
        PaymentConfirmationStatus expectedStatus)
    {
        var ok204 = responses.Count(r => r.StatusCode == HttpStatusCode.NoContent);
        var ok409 = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, ok204);
        Assert.Equal(1, ok409);
        Assert.Equal(1, auditDelta);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var status = db.PaymentLines.AsNoTracking().Single(p => p.Id == paymentLineId).ConfirmationStatus;
        var rejRows = db.RejectedCardToCard.Count(r => r.PaymentLineId == paymentLineId);
        Assert.Equal(expectedStatus, status);
        if (expectedStatus == PaymentConfirmationStatus.Rejected)
        {
            Assert.Equal(1, rejRows);
        }
        else
        {
            Assert.Equal(0, rejRows);
        }
    }

    [Theory]
    [InlineData("confirm", "confirm")]
    [InlineData("confirm", "reject")]
    [InlineData("reject", "reject")]
    public async Task concurrent_confirm_reject_races_produce_one_success_and_one_conflict(
        string firstAction,
        string secondAction)
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var op2 = await factory.CreateOperatorClientAsync();

        for (var i = 0; i < 20; i++)
        {
            var stationId = $"PS5-R21-{firstAction}{secondAction}{i}";
            var lineId = await CreatePendingCardToCardLineAsync(op, stationId);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var beforeAudit = db.AuditLog.Count(a => a.DetailsJson.Contains($"\"paymentLineId\":{lineId}"));

            Task<HttpResponseMessage> Act(HttpClient client, string action) =>
                client.PostAsync($"/api/operator/payments/{lineId}/{action}", null);

            var responses = await RaceAsync(() => Act(op, firstAction), () => Act(op2, secondAction));

            db.ChangeTracker.Clear();
            var auditDelta = db.AuditLog.Count(a => a.DetailsJson.Contains($"\"paymentLineId\":{lineId}")) - beforeAudit;
            var finalStatus = db.PaymentLines.AsNoTracking().Single(p => p.Id == lineId).ConfirmationStatus;

            var expectedStatus = (firstAction, secondAction) switch
            {
                ("confirm", "confirm") => PaymentConfirmationStatus.Confirmed,
                ("reject", "reject") => PaymentConfirmationStatus.Rejected,
                _ => finalStatus,
            };

            AssertRoundOutcome(lineId, responses, auditDelta, expectedStatus);
        }
    }

    [Fact]
    public async Task double_confirm_on_pending_line_returns_409_on_second()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var lineId = await CreatePendingCardToCardLineAsync(op, "PS5-R21-2xC");

        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/operator/payments/{lineId}/confirm", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsync($"/api/operator/payments/{lineId}/confirm", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsync($"/api/operator/payments/{lineId}/reject", null)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(PaymentConfirmationStatus.Confirmed, db.PaymentLines.Single(p => p.Id == lineId).ConfirmationStatus);
        Assert.Equal(1, db.AuditLog.Count(a => a.Action == "payment.card_to_card_confirmed" && a.DetailsJson.Contains($"\"paymentLineId\":{lineId}")));
        Assert.Empty(db.RejectedCardToCard.Where(r => r.PaymentLineId == lineId));
    }

    [Fact]
    public async Task double_reject_on_pending_line_returns_409_on_second()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var lineId = await CreatePendingCardToCardLineAsync(op, "PS5-R21-2xR");

        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/operator/payments/{lineId}/reject", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsync($"/api/operator/payments/{lineId}/reject", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsync($"/api/operator/payments/{lineId}/confirm", null)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(PaymentConfirmationStatus.Rejected, db.PaymentLines.Single(p => p.Id == lineId).ConfirmationStatus);
        Assert.Equal(1, db.AuditLog.Count(a => a.Action == "payment.card_to_card_rejected" && a.DetailsJson.Contains($"\"paymentLineId\":{lineId}")));
        Assert.Equal(1, db.RejectedCardToCard.Count(r => r.PaymentLineId == lineId));
    }
}
