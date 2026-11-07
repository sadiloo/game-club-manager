using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public sealed record WorkingDayInput(int Year, int Month, int Day);

public sealed record CloseSessionAction(Guid SessionId, string Action);

public sealed class OperatorShiftCloseService(
    AppDbContext db,
    IClock clock,
    AuditService audit,
    OperatorContext op,
    OperatorShiftGate shiftGate)
{
    public async Task<object> GetLiveStateAsync(CancellationToken ct)
    {
        var settings = await shiftGate.LoadSettingsAsync(ct);
        var openShift = await shiftGate.GetOpenShiftTrackedAsync(ct);
        DateTime? lastCloseUtc = null;
        if (!settings.ShiftsEnabled)
        {
            lastCloseUtc = await db.CloseRecords.AsNoTracking()
                .Where(c => c.ShiftId == null)
                .OrderByDescending(c => c.ClosedAtUtc)
                .Select(c => (DateTime?)c.ClosedAtUtc)
                .FirstOrDefaultAsync(ct);
        }

        return new
        {
            settings.ShiftsEnabled,
            settings.MultiOperatorEnabled,
            openShift = openShift is null
                ? null
                : new
                {
                    openShift.Id,
                    openShift.OpenedAtUtc,
                    openShift.OpeningFloatToman,
                    openShift.OpenedByUserId,
                },
            lastWorkingPeriodCloseUtc = lastCloseUtc,
        };
    }

    public async Task<object> UpdateSettingsAsync(bool? shiftsEnabled, bool? multiOperatorEnabled, CancellationToken ct)
    {
        if (op.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Only a manager may change cafe settings.");
        }

        var row = await db.BranchCafeSettings.FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new BranchCafeSettingsEntity();
            db.BranchCafeSettings.Add(row);
        }

        var before = new { row.ShiftsEnabled, row.MultiOperatorEnabled };
        if (shiftsEnabled is bool se)
        {
            row.ShiftsEnabled = se;
        }

        if (multiOperatorEnabled is bool mo)
        {
            row.MultiOperatorEnabled = mo;
        }

        await audit.AppendAsync("cafe.settings", new { before, after = new { row.ShiftsEnabled, row.MultiOperatorEnabled } }, ct);
        await db.SaveChangesAsync(ct);
        return new { row.ShiftsEnabled, row.MultiOperatorEnabled };
    }

    public async Task<object> OpenShiftAsync(long openingFloatToman, CancellationToken ct)
    {
        var settings = await shiftGate.LoadSettingsAsync(ct);
        if (!settings.ShiftsEnabled)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "shifts_disabled", "Shifts are disabled.");
        }

        if (openingFloatToman < 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_float", "Opening float must be non-negative.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.Shifts.AnyAsync(s => s.Status == ShiftStatus.Open, ct))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "shift_open", "A shift is already open.");
        }

        if (op.UserId is not Guid userId)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "unauthorized", "Operator login required.");
        }

        var shift = new ShiftEntity
        {
            Id = Guid.NewGuid(),
            Status = ShiftStatus.Open,
            OpenedAtUtc = clock.UtcNow.UtcDateTime,
            OpeningFloatToman = openingFloatToman,
            OpenedByUserId = userId,
        };
        db.Shifts.Add(shift);
        await audit.AppendAsync("shift.open", new { shift.Id, openingFloatToman }, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new { shift.Id, shift.OpenedAtUtc, shift.OpeningFloatToman };
    }

    public async Task<object> PreviewCloseAsync(CancellationToken ct)
    {
        var settings = await shiftGate.LoadSettingsAsync(ct);
        var shift = settings.ShiftsEnabled
            ? await shiftGate.GetOpenShiftTrackedAsync(ct)
                ?? throw new ApiException(StatusCodes.Status409Conflict, "no_open_shift", "No shift is open.")
            : null;

        var periodStart = shift?.OpenedAtUtc
            ?? await db.CloseRecords.AsNoTracking()
                .Where(c => c.ShiftId == null)
                .OrderByDescending(c => c.ClosedAtUtc)
                .Select(c => (DateTime?)c.PeriodOpenedAtUtc)
                .FirstOrDefaultAsync(ct)
            ?? DateTime.UnixEpoch;

        var expected = await BuildExpectedAsync(shift?.Id, periodStart, settings.ShiftsEnabled, ct);
        var openSessions = await ListOpenSessionsForCloseAsync(ct);
        var pendingC2c = await ListPendingC2cForPeriodAsync(shift?.Id, periodStart, ct);
        var pendingWallet = await ListPendingWalletForPeriodAsync(shift?.Id, periodStart, ct);
        var destinationBreakdown = await DestinationBreakdownAsync(shift?.Id, periodStart, ct);
        var auditFacts = await ShiftAuditFactsAsync(periodStart, ct);
        return new
        {
            settings.ShiftsEnabled,
            shiftId = shift?.Id,
            openingFloatToman = shift?.OpeningFloatToman ?? 0,
            expected = new
            {
                cashExpected = expected.CashExpected,
                cardReaderExpected = expected.CardReaderExpected,
                cardToCardConfirmedExpected = expected.CardToCardConfirmedExpected,
                cardToCardPending = expected.CardToCardPending,
            },
            openSessions,
            pendingCardToCard = pendingC2c,
            pendingWalletTopUps = pendingWallet,
            destinationCards = destinationBreakdown,
            audit = auditFacts,
        };
    }

    public async Task<object> CloseAsync(
        WorkingDayInput workingDay,
        long actualCashToman,
        long actualCardReaderToman,
        string? discrepancyExplanation,
        IReadOnlyList<CloseSessionAction> sessionActions,
        Guid? nextRecipientUserId,
        CancellationToken ct)
    {
        _ = new LocalDate(workingDay.Year, workingDay.Month, workingDay.Day);
        var settings = await shiftGate.LoadSettingsAsync(ct);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            ShiftEntity? shift = null;
            DateTime periodStart;
            if (settings.ShiftsEnabled)
            {
                shift = await db.Shifts.FirstOrDefaultAsync(s => s.Status == ShiftStatus.Open, ct)
                    ?? throw new ApiException(StatusCodes.Status409Conflict, "no_open_shift", "No shift is open.");
                await db.Entry(shift).ReloadAsync(ct);
                if (shift.Status != ShiftStatus.Open)
                {
                    throw new ApiException(StatusCodes.Status409Conflict, "already_closed", "Shift is already closed.");
                }

                periodStart = shift.OpenedAtUtc;
            }
            else
            {
                periodStart = await db.CloseRecords.AsNoTracking()
                    .Where(c => c.ShiftId == null)
                    .OrderByDescending(c => c.ClosedAtUtc)
                    .Select(c => (DateTime?)c.PeriodOpenedAtUtc)
                    .FirstOrDefaultAsync(ct)
                    ?? DateTime.UnixEpoch;
            }

            var expected = await BuildExpectedAsync(shift?.Id, periodStart, settings.ShiftsEnabled, ct);
            var discrepancies = new[]
            {
                actualCashToman - expected.CashExpected,
                actualCardReaderToman - expected.CardReaderExpected,
            };
            if (discrepancies.Any(d => d != 0) && string.IsNullOrWhiteSpace(discrepancyExplanation))
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "explanation_required", "Discrepancy explanation is required.");
            }

            var closeId = Guid.NewGuid();
            var closeRecord = new CloseRecordEntity
            {
                Id = closeId,
                ShiftId = shift?.Id,
                PeriodOpenedAtUtc = periodStart,
                ClosedAtUtc = clock.UtcNow.UtcDateTime,
                WorkingDayYear = workingDay.Year,
                WorkingDayMonth = workingDay.Month,
                WorkingDayDay = workingDay.Day,
                ActualCashToman = actualCashToman,
                ActualCardReaderToman = actualCardReaderToman,
                DiscrepancyExplanation = discrepancyExplanation?.Trim(),
                NextRecipientUserId = nextRecipientUserId,
                ClosedByUserId = op.UserId ?? throw new ApiException(StatusCodes.Status401Unauthorized, "unauthorized", "Operator login required."),
            };
            db.CloseRecords.Add(closeRecord);

            foreach (var action in sessionActions)
            {
                if (action.Action == "carry")
                {
                    var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == action.SessionId, ct)
                        ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Session not found.");
                    session.CarriedAtCloseRecordId = closeId;
                }
                else if (action.Action == "settle")
                {
                    throw new ApiException(
                        StatusCodes.Status400BadRequest,
                        "settle_via_confirm",
                        "Settle open sessions via bill confirm before close.");
                }
            }

            if (shift is not null)
            {
                shift.Status = ShiftStatus.Closed;
                shift.ClosedAtUtc = closeRecord.ClosedAtUtc;
            }

            await StampPeriodInvoicesAsync(shift?.Id, periodStart, closeRecord, ct);
            await audit.AppendAsync(
                settings.ShiftsEnabled ? "shift.close" : "working_day.close",
                new
                {
                    closeRecord.Id,
                    shiftId = shift?.Id,
                    workingDay,
                    actualCashToman,
                    actualCardReaderToman,
                    expected,
                    discrepancies,
                },
                ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new
            {
                closeRecordId = closeRecord.Id,
                shiftId = shift?.Id,
                workingDay,
                expected,
                actualCashToman,
                actualCardReaderToman,
            };
        });
    }

    private async Task StampPeriodInvoicesAsync(Guid? shiftId, DateTime periodStart, CloseRecordEntity close, CancellationToken ct)
    {
        IQueryable<InvoiceEntity> query = db.Invoices.Where(i => i.CloseRecordId == null && i.Status != "void");
        if (shiftId is Guid sid)
        {
            query = query.Where(i => i.ShiftId == sid);
        }
        else
        {
            query = query.Where(i => i.IssuedAtUtc >= periodStart);
        }

        var invoices = await query.ToListAsync(ct);
        foreach (var invoice in invoices)
        {
            invoice.CloseRecordId = close.Id;
            invoice.WorkingDayYear = close.WorkingDayYear;
            invoice.WorkingDayMonth = close.WorkingDayMonth;
            invoice.WorkingDayDay = close.WorkingDayDay;
        }

        IQueryable<WalletLedgerEntryEntity> walletQuery = db.WalletLedger
            .Where(w => w.Kind == WalletLedgerKind.TopUpReal);
        if (shiftId is Guid sid2)
        {
            walletQuery = walletQuery.Where(w => w.ShiftId == sid2);
        }
        else
        {
            walletQuery = walletQuery.Where(w => w.AtUtc >= periodStart);
        }

        foreach (var row in await walletQuery.ToListAsync(ct))
        {
            row.WorkingDayYear = close.WorkingDayYear;
            row.WorkingDayMonth = close.WorkingDayMonth;
            row.WorkingDayDay = close.WorkingDayDay;
        }
    }

    private sealed record ExpectedTotals(long CashExpected, long CardReaderExpected, long CardToCardConfirmedExpected, long CardToCardPending);

    private async Task<ExpectedTotals> BuildExpectedAsync(Guid? shiftId, DateTime periodStart, bool shiftsEnabled, CancellationToken ct)
    {
        long opening = 0;
        if (shiftsEnabled && shiftId is Guid sid)
        {
            opening = await db.Shifts.AsNoTracking().Where(s => s.Id == sid).Select(s => s.OpeningFloatToman).FirstAsync(ct);
        }

        IQueryable<InvoiceEntity> invQ = db.Invoices.AsNoTracking()
            .Include(i => i.PaymentLines)
            .Where(i => i.Status == "confirmed");
        if (shiftId is Guid shift)
        {
            invQ = invQ.Where(i => i.ShiftId == shift);
        }
        else
        {
            invQ = invQ.Where(i => i.CloseRecordId == null && i.IssuedAtUtc >= periodStart);
        }

        var invoices = await invQ.ToListAsync(ct);
        long cash = opening;
        long card = 0;
        long c2cConfirmed = 0;
        long c2cPending = 0;
        foreach (var invoice in invoices)
        {
            foreach (var line in invoice.PaymentLines)
            {
                if (line.Method == PaymentMethod.Cash)
                {
                    cash = MoneyMath.CheckedAdd(cash, line.AmountToman);
                }
                else if (line.Method == PaymentMethod.CardReader)
                {
                    card = MoneyMath.CheckedAdd(card, line.AmountToman);
                }
                else if (line.Method == PaymentMethod.CardToCard)
                {
                    if (line.ConfirmationStatus == PaymentConfirmationStatus.Pending)
                    {
                        c2cPending = MoneyMath.CheckedAdd(c2cPending, line.AmountToman);
                    }
                    else if (line.ConfirmationStatus == PaymentConfirmationStatus.Confirmed
                             || line.ConfirmationStatus == PaymentConfirmationStatus.Settled)
                    {
                        c2cConfirmed = MoneyMath.CheckedAdd(c2cConfirmed, line.AmountToman);
                    }
                }
            }
        }

        IQueryable<WalletLedgerEntryEntity> topUpQ = db.WalletLedger.AsNoTracking()
            .Where(w => w.Kind == WalletLedgerKind.TopUpReal);
        if (shiftId is Guid s)
        {
            topUpQ = topUpQ.Where(w => w.ShiftId == s);
        }
        else
        {
            topUpQ = topUpQ.Where(w => w.AtUtc >= periodStart && w.ShiftId == null);
        }

        foreach (var row in await topUpQ.ToListAsync(ct))
        {
            switch (row.Method)
            {
                case PaymentMethod.Cash:
                    cash = MoneyMath.CheckedAdd(cash, row.RealDeltaToman);
                    break;
                case PaymentMethod.CardReader:
                    card = MoneyMath.CheckedAdd(card, row.RealDeltaToman);
                    break;
                case PaymentMethod.CardToCard:
                    c2cConfirmed = MoneyMath.CheckedAdd(c2cConfirmed, row.RealDeltaToman);
                    break;
            }
        }

        return new ExpectedTotals(cash, card, c2cConfirmed, c2cPending);
    }

    private async Task<IReadOnlyList<object>> ListOpenSessionsForCloseAsync(CancellationToken ct)
    {
        var rows = await db.Sessions.AsNoTracking()
            .Include(s => s.Station)
            .Where(s => s.Status != SessionStatus.Closed)
            .OrderBy(s => s.StartedAtUtc)
            .Select(s => new { sessionId = s.Id, stationId = s.StationId, status = s.Status.ToString() })
            .ToListAsync(ct);
        return rows.Cast<object>().ToList();
    }

    private async Task<IReadOnlyList<object>> ListPendingC2cForPeriodAsync(Guid? shiftId, DateTime periodStart, CancellationToken ct)
    {
        var q = from p in db.PaymentLines.AsNoTracking()
                join i in db.Invoices.AsNoTracking() on p.InvoiceId equals i.Id
                where p.Method == PaymentMethod.CardToCard
                      && p.ConfirmationStatus == PaymentConfirmationStatus.Pending
                select new { p, i };
        if (shiftId is Guid s)
        {
            q = q.Where(x => x.i.ShiftId == s);
        }
        else
        {
            q = q.Where(x => x.i.IssuedAtUtc >= periodStart && x.i.CloseRecordId == null);
        }

        return (await q.Select(x => new
        {
            paymentLineId = x.p.Id,
            invoiceNumber = x.i.Number,
            x.p.AmountToman,
            x.p.DestinationCardId,
            x.p.Reference,
        }).ToListAsync(ct)).Cast<object>().ToList();
    }

    private async Task<IReadOnlyList<object>> ListPendingWalletForPeriodAsync(Guid? shiftId, DateTime periodStart, CancellationToken ct)
    {
        var q = db.PendingWalletTopUps.AsNoTracking()
            .Where(p => p.Status == PendingWalletTopUpStatus.Pending);
        if (shiftId is Guid s)
        {
            q = q.Where(p => p.ShiftId == s);
        }
        else
        {
            q = q.Where(p => p.CreatedAtUtc >= periodStart);
        }

        return (await q.Select(p => new { p.Id, p.AmountToman, method = p.Method.ToString(), p.DestinationCardId }).ToListAsync(ct))
            .Cast<object>()
            .ToList();
    }

    private async Task<IReadOnlyList<object>> DestinationBreakdownAsync(Guid? shiftId, DateTime periodStart, CancellationToken ct)
    {
        var q = from p in db.PaymentLines.AsNoTracking()
                join i in db.Invoices.AsNoTracking() on p.InvoiceId equals i.Id
                where p.Method == PaymentMethod.CardToCard && p.DestinationCardId != null
                select new { p, i };
        if (shiftId is Guid s)
        {
            q = q.Where(x => x.i.ShiftId == s);
        }
        else
        {
            q = q.Where(x => x.i.IssuedAtUtc >= periodStart);
        }

        return (await q.GroupBy(x => x.p.DestinationCardId)
            .Select(g => new { destinationCardId = g.Key, totalToman = g.Sum(x => x.p.AmountToman) })
            .ToListAsync(ct)).Cast<object>().ToList();
    }

    private async Task<object> ShiftAuditFactsAsync(DateTime periodStart, CancellationToken ct)
    {
        var cancellations = await db.AuditLog.AsNoTracking()
            .Where(a => a.AtUtc >= periodStart && (a.Action == "bill.cancel" || a.Action.Contains("discount")))
            .CountAsync(ct);
        var stationCancelCounts = await db.AuditLog.AsNoTracking()
            .Where(a => a.AtUtc >= periodStart && a.Action == "session.stop" && a.DetailsJson.Contains("cancel"))
            .CountAsync(ct);
        return new { cancellations, stationCancelCounts, ss17PerStation = Array.Empty<object>() };
    }
}
