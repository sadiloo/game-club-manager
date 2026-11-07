using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public sealed record BillAmounts(
    long UnroundedPayableToman,
    long RoundingToman,
    long CollectibleToman,
    long SurchargeToman,
    DateTime BilledToUtc);

public sealed class BillCalculator(SessionBilling billing, RateConfigService rates)
{
    public async Task<BillAmounts> ComputeAsync(
        PlaySessionEntity session,
        DateTime endUtc,
        long surchargeToman,
        CancellationToken ct)
    {
        var snapshot = session.RateCardSnapshotId != 0
            ? await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct)
            : await rates.LoadAsync(ct);
        var charge = billing.BuildCharge(session, endUtc, snapshot);
        var timeLines = TimeChargeLine.FromSession(charge);
        IReadOnlyList<ServiceLine> services = surchargeToman > 0
            ? [new ServiceLine("manual-surcharge", "سورشارژ", surchargeToman)]
            : [];
        var issuedAt = TimeMapping.ToTehran(endUtc);
        var calc = InvoiceCalculator.Calculate(new InvoiceRequest(
            timeLines,
            [],
            services,
            null,
            null,
            DiscountPolicy.Default,
            snapshot.Rounding,
            issuedAt));
        return new BillAmounts(
            calc.NetBeforeRoundingToman,
            calc.RoundingToman,
            calc.PayableToman,
            surchargeToman,
            endUtc);
    }
}

public sealed class OperatorBillService(
    AppDbContext db,
    IClock clock,
    AuditService audit,
    BillCalculator calculator,
    SessionBilling billing,
    SessionCloser closer,
    OperatorPolicies policies)
{
    public sealed record PaymentInput(string Method, long AmountToman, string? DestinationCardId, string? Reference);

    public async Task<PendingBillEntity> OpenConsoleBillAsync(Guid sessionId, CancellationToken ct)
    {
        return await WithSessionBillLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Station!.Group!.Kind != StationKind.Console)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "not_console", "Bill open applies to consoles.");
            }

            if (session.Status is not SessionStatus.Open and not SessionStatus.Paused)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "invalid_state", "Session cannot open a bill.");
            }

            if (session.PendingBill is not null)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "bill_open", "Bill is already open.");
            }

            var frozenAt = clock.UtcNow.UtcDateTime;
            var revert = CaptureRevertState(session);
            ApplyPlayEnd(session, frozenAt);
            var amounts = await calculator.ComputeAsync(session, frozenAt, 0, innerCt);
            var bill = new PendingBillEntity
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                FrozenAtUtc = frozenAt,
                OpenedAtUtc = frozenAt,
                UnroundedPayableToman = amounts.UnroundedPayableToman,
                RoundingToman = amounts.RoundingToman,
                CollectibleToman = amounts.CollectibleToman,
                Source = "console_bill",
                RevertStateJson = JsonSerializer.Serialize(revert),
            };
            session.Status = SessionStatus.BillOpen;
            session.PendingBill = bill;
            db.PendingBills.Add(bill);
            await audit.AppendAsync("bill.open", new { sessionId, frozenAt, revert }, innerCt);
            return bill;
        }, ct);
    }

    public async Task CancelConsoleBillAsync(Guid sessionId, CancellationToken ct)
    {
        await WithSessionBillLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Status != SessionStatus.BillOpen || session.PendingBill is null)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "no_bill", "No open bill to cancel.");
            }

            var bill = session.PendingBill;
            db.PendingBills.Remove(bill);
            session.PendingBill = null;
            RestoreRevertState(session, bill);
            await audit.AppendAsync("bill.cancel", new { sessionId }, innerCt);
        }, ct);
    }

    public async Task AddSurchargeAsync(Guid sessionId, string? reason, CancellationToken ct)
    {
        await WithSessionBillLockAsync(sessionId, async (session, innerCt) =>
        {
            var bill = session.PendingBill
                ?? throw new ApiException(StatusCodes.Status409Conflict, "no_bill", "No open bill.");
            bill.SurchargeToman = MoneyMath.CheckedAdd(bill.SurchargeToman, 5_000);
            var amounts = await calculator.ComputeAsync(session, bill.FrozenAtUtc, bill.SurchargeToman, innerCt);
            bill.UnroundedPayableToman = amounts.UnroundedPayableToman;
            bill.RoundingToman = amounts.RoundingToman;
            bill.CollectibleToman = amounts.CollectibleToman;
            await audit.AppendAsync("bill.surcharge", new { sessionId, reason, bill.SurchargeToman }, innerCt);
        }, ct);
    }

    public async Task<InvoiceEntity> ConfirmBillAsync(Guid sessionId, IReadOnlyList<PaymentInput> payments, CancellationToken ct)
    {
        return await WithSessionBillLockAsync(sessionId, async (session, innerCt) =>
        {
            var bill = session.PendingBill
                ?? throw new ApiException(StatusCodes.Status409Conflict, "no_bill", "No bill awaiting confirmation.");
            if (session.Status is not SessionStatus.BillOpen and not SessionStatus.AwaitingPayment)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "invalid_state", "Session is not awaiting payment.");
            }

            if (bill.CollectibleToman == 0)
            {
                if (payments.Count > 0)
                {
                    if (payments.Any(p => p.AmountToman > 0))
                    {
                        var positiveTenders = payments
                            .Select(p => new Tender(OperatorPaymentMethods.Parse(p.Method), p.AmountToman))
                            .ToList();
                        var positiveSplit = PaymentSplitter.Split(0, positiveTenders);
                        if (!positiveSplit.IsValid)
                        {
                            throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", positiveSplit.Errors.FirstOrDefault()?.Message ?? "Invalid payment.");
                        }
                    }

                    throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "At least one positive payment line is required.");
                }
            }
            else
            {
                if (payments.Count == 0 || payments.All(p => p.AmountToman <= 0))
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "At least one positive payment line is required.");
                }

                var tenders = payments.Select(p => new Tender(OperatorPaymentMethods.Parse(p.Method), p.AmountToman)).ToList();
                var split = PaymentSplitter.Split(bill.CollectibleToman, tenders);
                if (!split.IsValid)
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", split.Errors.FirstOrDefault()?.Message ?? "Invalid payment.");
                }
            }

            var invoice = await closer.FinalizeFromPendingAsync(session, bill, payments, innerCt);
            await audit.AppendAsync("bill.confirm", new { sessionId, invoiceNumber = invoice.Number, collectible = invoice.CollectibleToman }, innerCt);
            return invoice;
        }, ct);
    }

    public async Task<PendingBillEntity> CreatePcPendingBillAsync(
        PlaySessionEntity session,
        DateTime endUtc,
        string source,
        string? stopReason,
        CancellationToken ct)
    {
        ApplyPlayEnd(session, endUtc);
        var amounts = await calculator.ComputeAsync(session, endUtc, 0, ct);
        var bill = new PendingBillEntity
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            FrozenAtUtc = endUtc,
            OpenedAtUtc = clock.UtcNow.UtcDateTime,
            UnroundedPayableToman = amounts.UnroundedPayableToman,
            RoundingToman = amounts.RoundingToman,
            CollectibleToman = amounts.CollectibleToman,
            Source = source,
            StopReason = stopReason,
        };
        session.Status = SessionStatus.AwaitingPayment;
        session.PendingBill = bill;
        db.PendingBills.Add(bill);
        return bill;
    }

    public async Task<PendingBillEntity> CreateMachineStopPendingBillAsync(
        PlaySessionEntity session,
        DateTime endUtc,
        CancellationToken ct)
    {
        ApplyPlayEnd(session, endUtc);
        var amounts = await calculator.ComputeAsync(session, endUtc, 0, ct);
        var bill = new PendingBillEntity
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            FrozenAtUtc = endUtc,
            OpenedAtUtc = clock.UtcNow.UtcDateTime,
            UnroundedPayableToman = amounts.UnroundedPayableToman,
            RoundingToman = amounts.RoundingToman,
            CollectibleToman = amounts.CollectibleToman,
            Source = "console_stop",
            StopReason = "stop",
        };
        session.Status = SessionStatus.AwaitingPayment;
        session.PendingBill = bill;
        db.PendingBills.Add(bill);
        return bill;
    }

    public async Task<object> GetBillDetailAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking()
            .Include(s => s.Segments)
            .Include(s => s.PendingBill)
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Session not found.");
        var bill = session.PendingBill
            ?? throw new ApiException(StatusCodes.Status409Conflict, "no_bill", "No bill for this session.");
        var charge = await billing.BuildChargeAsync(session, bill.FrozenAtUtc, ct);
        var hourlyBySegment = charge.Lines
            .GroupBy(l => l.SegmentId)
            .ToDictionary(g => g.Key, g => g.First().HourlyRateToman);
        var segments = session.Segments
            .OrderBy(s => s.StartUtc)
            .Select(s => new
            {
                s.SegmentKey,
                s.PlayerCount,
                mode = s.Mode.ToString(),
                hourlyToman = hourlyBySegment.GetValueOrDefault(s.SegmentKey),
                startUtc = s.StartUtc,
                endUtc = s.EndUtc,
            })
            .ToList();
        return new
        {
            bill.SessionId,
            playerCount = session.PlayerCount,
            groupId = session.Station!.GroupId,
            unroundedPayableToman = bill.UnroundedPayableToman,
            roundingToman = bill.RoundingToman,
            collectibleToman = bill.CollectibleToman,
            surchargeToman = bill.SurchargeToman,
            frozenAtUtc = bill.FrozenAtUtc,
            source = bill.Source,
            stopReason = bill.StopReason,
            segments,
        };
    }

    public async Task<PendingBillEntity?> GetPendingAsync(Guid sessionId, CancellationToken ct)
        => await db.PendingBills.AsNoTracking().FirstOrDefaultAsync(b => b.SessionId == sessionId, ct);

    private static void ApplyPlayEnd(PlaySessionEntity session, DateTime endUtc)
    {
        foreach (var seg in session.Segments.Where(s => s.EndUtc is null))
        {
            seg.EndUtc = endUtc;
        }

        foreach (var pause in session.Pauses.Where(p => p.EndUtc is null))
        {
            pause.EndUtc = endUtc;
        }

        session.EndedAtUtc = endUtc;
    }

    private sealed record RevertSnapshot(string Status, List<SegmentRevert> Segments, List<PauseRevert> Pauses);

    private sealed record SegmentRevert(long Id, DateTime? EndUtc);

    private sealed record PauseRevert(long Id, DateTime? EndUtc);

    private static RevertSnapshot CaptureRevertState(PlaySessionEntity session)
        => new(
            session.Status.ToString(),
            session.Segments.Select(s => new SegmentRevert(s.Id, s.EndUtc)).ToList(),
            session.Pauses.Select(p => new PauseRevert(p.Id, p.EndUtc)).ToList());

    private static void RestoreRevertState(PlaySessionEntity session, PendingBillEntity bill)
    {
        if (string.IsNullOrWhiteSpace(bill.RevertStateJson))
        {
            return;
        }

        var revert = JsonSerializer.Deserialize<RevertSnapshot>(bill.RevertStateJson)
            ?? throw new ApiException(StatusCodes.Status500InternalServerError, "revert_missing", "Bill revert state is missing.");
        session.Status = Enum.Parse<SessionStatus>(revert.Status);
        session.EndedAtUtc = null;
        foreach (var seg in revert.Segments)
        {
            session.Segments.First(s => s.Id == seg.Id).EndUtc = seg.EndUtc;
        }

        foreach (var pause in revert.Pauses)
        {
            session.Pauses.First(p => p.Id == pause.Id).EndUtc = pause.EndUtc;
        }
    }

    private async Task<T> WithSessionBillLockAsync<T>(
        Guid sessionId,
        Func<PlaySessionEntity, CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    """SELECT "Id" FROM sessions WHERE "Id" = {0} FOR UPDATE NOWAIT""",
                    sessionId);
            }
            catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "session_busy", "Session is being updated.");
            }

            var session = await db.Sessions
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Include(s => s.Segments)
                .Include(s => s.Pauses)
                .Include(s => s.PendingBill)
                .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Session not found.");
            try
            {
                var result = await action(session, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateConcurrencyException) when (policies.ConcurrentBillActionReturns409)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "stale_bill", "Bill was updated elsewhere; refresh and retry.");
            }
        });
    }

    private Task WithSessionBillLockAsync(
        Guid sessionId,
        Func<PlaySessionEntity, CancellationToken, Task> action,
        CancellationToken ct)
        => WithSessionBillLockAsync(sessionId, async (s, inner) =>
        {
            await action(s, inner);
            return false;
        }, ct);
}

public sealed class OperatorGridService(AppDbContext db, SessionService sessions)
{
    public async Task<IReadOnlyList<object>> GetGridAsync(CancellationToken ct)
    {
        var stations = await db.Stations.AsNoTracking()
            .Include(s => s.Group)
            .Where(s => s.IsActive)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);
        var openSessions = await db.Sessions.AsNoTracking()
            .Include(s => s.PendingBill)
            .Where(s => s.Status != SessionStatus.Closed)
            .ToListAsync(ct);
        var byStation = openSessions
            .GroupBy(s => s.StationId)
            .ToDictionary(g => g.Key, g => PickTileSession(g.ToList()));

        var tiles = new List<object>();
        foreach (var station in stations)
        {
            if (!byStation.TryGetValue(station.Id, out var session))
            {
                tiles.Add(new
                {
                    station.Id,
                    station.Name,
                    station.GroupId,
                    station.DefaultPlayerCount,
                    mode = station.DefaultPlayMode.ToString(),
                    status = "Free",
                    amountToman = (long?)null,
                    sessionId = (Guid?)null,
                });
                continue;
            }

            var status = MapStatus(session);
            long? amount = null;
            if (status is "Playing" or "Paused")
            {
                var loaded = await db.Sessions
                    .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                    .Include(s => s.Segments)
                    .Include(s => s.Pauses)
                    .FirstAsync(s => s.Id == session.Id, ct);
                amount = await sessions.LiveAmountAsync(loaded, ct);
            }
            else if (session.PendingBill is not null)
            {
                amount = session.PendingBill.CollectibleToman;
            }

            tiles.Add(new
            {
                station.Id,
                station.Name,
                station.GroupId,
                station.DefaultPlayerCount,
                mode = station.DefaultPlayMode.ToString(),
                status,
                amountToman = amount,
                sessionId = session.Id,
            });
        }

        return tiles;
    }

    private static PlaySessionEntity PickTileSession(IReadOnlyList<PlaySessionEntity> onStation)
    {
        if (onStation.Count == 1)
        {
            return onStation[0];
        }

        static int Priority(SessionStatus status) => status switch
        {
            SessionStatus.Open => 0,
            SessionStatus.Paused => 1,
            SessionStatus.BillOpen => 2,
            SessionStatus.AwaitingPayment => 3,
            SessionStatus.ManualSettle => 4,
            _ => 5,
        };

        return onStation
            .OrderBy(s => Priority(s.Status))
            .ThenByDescending(s => s.StartedAtUtc)
            .First();
    }

    private static string MapStatus(PlaySessionEntity session) => session.Status switch
    {
        SessionStatus.Open => "Playing",
        SessionStatus.Paused => "Paused",
        SessionStatus.BillOpen or SessionStatus.AwaitingPayment => "Waiting for payment",
        SessionStatus.ManualSettle => "Manual settle",
        _ => "Free",
    };
}

public sealed class OperatorInvoiceService(
    AppDbContext db,
    AuditService audit,
    OperatorContext context,
    OperatorPolicies policies)
{
    public async Task<InvoiceEntity> GetAsync(long number, CancellationToken ct)
    {
        var invoice = await db.Invoices.AsNoTracking()
            .Include(i => i.PaymentLines)
            .Include(i => i.Session)!.ThenInclude(s => s!.Station)
            .FirstOrDefaultAsync(i => i.Number == number, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Invoice not found.");
        return invoice;
    }

    public async Task VoidAsync(long number, string? reason, CancellationToken ct)
    {
        if (context.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Only a manager may void an invoice.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "reason_required", "Void reason is required.");
        }

        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Number == number, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Invoice not found.");
        if (invoice.Status == "void")
        {
            return;
        }

        var before = new { invoice.Status, invoice.CollectibleToman };
        invoice.Status = "void";
        invoice.VoidReason = reason.Trim();
        await audit.AppendAsync("invoice.void", new { number, reason, before, after = new { invoice.Status } }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<object>> ListPendingCardToCardAsync(CancellationToken ct)
    {
        var rows = await db.PaymentLines.AsNoTracking()
            .Include(p => p.Invoice)
            .Where(p => p.Method == PaymentMethod.CardToCard && p.ConfirmationStatus == PaymentConfirmationStatus.Pending)
            .OrderBy(p => p.Id)
            .Select(p => new PendingCardToCardRow(
                p.Id,
                p.Invoice!.Number,
                p.AmountToman,
                p.DestinationCardId,
                p.Reference,
                p.Invoice!.SessionId))
            .ToListAsync(ct);
        return rows.Cast<object>().ToList();
    }

    private sealed record PendingCardToCardRow(
        long Id,
        long InvoiceNumber,
        long AmountToman,
        string? DestinationCardId,
        string? Reference,
        Guid SessionId);

    public async Task ConfirmCardToCardAsync(long paymentLineId, CancellationToken ct)
    {
        if (!policies.OperatorMayConfirmCardToCard)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Card-to-card confirmation is disabled.");
        }

        var updated = await db.PaymentLines
            .Where(p => p.Id == paymentLineId && p.ConfirmationStatus == PaymentConfirmationStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.ConfirmationStatus, PaymentConfirmationStatus.Confirmed),
                ct);
        if (updated == 0)
        {
            var exists = await db.PaymentLines.AsNoTracking().AnyAsync(p => p.Id == paymentLineId, ct);
            if (!exists)
            {
                throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Payment line not found.");
            }

            throw new ApiException(StatusCodes.Status409Conflict, "not_pending", "Payment is not pending.");
        }

        await audit.AppendAsync("payment.card_to_card_confirmed", new { paymentLineId }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task RejectCardToCardAsync(long paymentLineId, CancellationToken ct)
    {
        if (!policies.OperatorMayConfirmCardToCard)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Card-to-card confirmation is disabled.");
        }

        var updated = await db.PaymentLines
            .Where(p => p.Id == paymentLineId && p.ConfirmationStatus == PaymentConfirmationStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.ConfirmationStatus, PaymentConfirmationStatus.Rejected),
                ct);
        if (updated == 0)
        {
            var exists = await db.PaymentLines.AsNoTracking().AnyAsync(p => p.Id == paymentLineId, ct);
            if (!exists)
            {
                throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Payment line not found.");
            }

            throw new ApiException(StatusCodes.Status409Conflict, "not_pending", "Payment is not pending.");
        }

        var line = await db.PaymentLines.AsNoTracking().FirstAsync(p => p.Id == paymentLineId, ct);
        if (policies.RejectedCardToCardGoesToManagerUnpaidList)
        {
            db.RejectedCardToCard.Add(new RejectedCardToCardEntity
            {
                PaymentLineId = line.Id,
                AmountToman = line.AmountToman,
                RecordedAtUtc = DateTime.UtcNow,
            });
        }

        await audit.AppendAsync("payment.card_to_card_rejected", new { paymentLineId }, ct);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class ManualSettleQueueService(AppDbContext db, OperatorPolicies policies, OperatorContext context)
{
    public async Task<IReadOnlyList<object>> ListAsync(CancellationToken ct)
    {
        var rows = await db.Sessions.AsNoTracking()
            .Include(s => s.Station)
            .Where(s => s.Status == SessionStatus.ManualSettle)
            .OrderBy(s => s.StartedAtUtc)
            .Select(s => new
            {
                sessionId = s.Id,
                stationId = s.StationId,
                startedAtUtc = s.StartedAtUtc,
                lastCheckpointUtc = s.LastCheckpointUtc,
                reason = s.ManualSettleReason,
            })
            .ToListAsync(ct);
        return rows.Cast<object>().ToList();
    }

    public void EnsureMayResolve()
    {
        if (!policies.OperatorMayResolveManualSettle && context.Role == UserRole.Operator)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Manual settle resolution is manager-only.");
        }
    }
}
