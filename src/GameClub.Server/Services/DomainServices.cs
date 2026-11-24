using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Hubs;
using GameClub.Server.Operator;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GameClub.Server.Services;

public static class TimeMapping
{
    public static DateTimeOffset ToTehran(DateTime utc)
        => TehranTime.FromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public static DateTime ToUtc(DateTimeOffset tehran) => TehranTime.Ensure(tehran).UtcDateTime;
}

public sealed class AuditService(AppDbContext db, IClock clock, Operator.OperatorContext operatorContext)
{
    public async Task AppendAsync(string action, object details, CancellationToken ct = default)
    {
        Stage(action, details);
        await db.SaveChangesAsync(ct);
    }

    public void Stage(string action, object details)
    {
        db.AuditLog.Add(new AuditLogEntry
        {
            AtUtc = clock.UtcNow.UtcDateTime,
            Action = action,
            DetailsJson = JsonSerializer.Serialize(details),
            ActorUserId = operatorContext.UserId,
        });
    }
}

public sealed class StationService(AppDbContext db, AuditService audit)
{
    public async Task<IReadOnlyList<StationEntity>> ListAsync(CancellationToken ct)
        => await db.Stations.AsNoTracking().Include(s => s.Group).OrderBy(s => s.Id).ToListAsync(ct);

    public async Task<StationEntity> GetAsync(string stationId, CancellationToken ct)
    {
        var id = StationIdNormalizer.Normalize(stationId);
        return await db.Stations.AsNoTracking().Include(s => s.Group).FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "ایستگاه یافت نشد.");
    }

    public async Task<StationEntity> RegisterAsync(string rawId, string name, string groupId, CancellationToken ct)
    {
        var id = StationIdNormalizer.Normalize(rawId);
        if (string.IsNullOrEmpty(id))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_station", "شناسهٔ ایستگاه الزامی است.");
        }

        var group = await db.StationGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "unknown_group", $"گروه «{groupId}» ناشناخته است.");
        }

        if (await db.Stations.AnyAsync(s => s.Id == id, ct))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "duplicate_station", $"ایستگاه «{id}» از قبل وجود دارد.");
        }

        try
        {
            var station = new StationEntity { Id = id, Name = name, GroupId = groupId };
            db.Stations.Add(station);
            await audit.AppendAsync("station.register", new { stationId = id, groupId }, ct);
            return station;
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsStationIdUniqueViolation(ex))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "duplicate_station", $"ایستگاه «{id}» از قبل وجود دارد.");
        }
    }

    public async Task<StationEntity> UpdateAsync(string stationId, string? name, string? groupId, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var id = StationIdNormalizer.Normalize(stationId);
            await db.Database.ExecuteSqlRawAsync(
                """SELECT 1 FROM stations WHERE "Id" = {0} FOR UPDATE""",
                id);
            var station = await db.Stations.FirstOrDefaultAsync(s => s.Id == id, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "ایستگاه یافت نشد.");
            if (!station.IsActive)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "inactive", "ایستگاه غیرفعال است.");
            }

            if (name is not null)
            {
                station.Name = name;
            }

            if (groupId is not null)
            {
                _ = await db.StationGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct)
                    ?? throw new ApiException(StatusCodes.Status400BadRequest, "unknown_group", $"گروه «{groupId}» ناشناخته است.");
            }

            if (groupId is not null && !string.Equals(groupId, station.GroupId, StringComparison.Ordinal))
            {
                if (await db.Sessions.AnyAsync(s => s.StationId == id && s.Status != SessionStatus.Closed, ct))
                {
                    throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "ایستگاه جلسهٔ فعال دارد؛ گروه عوض نمی‌شود.");
                }

                station.GroupId = groupId;
            }

            await audit.AppendAsync("station.update", new { stationId = id, name, groupId }, ct);
            await tx.CommitAsync(ct);
            return station;
        });
    }

    public async Task<StationEntity> DeactivateAsync(string stationId, CancellationToken ct)
    {
        var id = StationIdNormalizer.Normalize(stationId);
        var station = await db.Stations.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "ایستگاه یافت نشد.");
        if (!station.IsActive)
        {
            return station;
        }

        station.IsActive = false;
        await audit.AppendAsync("station.deactivate", new { stationId = id }, ct);
        await db.SaveChangesAsync(ct);
        return station;
    }

    public async Task<StationEntity> UpdateGroupAsync(string stationId, string groupId, CancellationToken ct)
        => await UpdateAsync(stationId, null, groupId, ct);
}

public sealed class SessionBilling(AppDbContext db, RateConfigService rates)
{
    public async Task<SessionCharge> BuildChargeAsync(PlaySessionEntity session, DateTime endUtc, CancellationToken ct = default)
    {
        var snapshot = await LoadSnapshotForSessionAsync(session, ct);
        return BuildCharge(session, endUtc, snapshot);
    }

    private async Task<BranchRatesSnapshot> LoadSnapshotForSessionAsync(PlaySessionEntity session, CancellationToken ct)
    {
        if (session.RateCardSnapshotId != 0)
        {
            return await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct);
        }

        return await rates.LoadAsync(ct);
    }

    public SessionCharge BuildCharge(PlaySessionEntity session, DateTime endUtc, BranchRatesSnapshot snapshot)
    {
        var directory = BuildDirectory();
        var segments = new List<PlaySegment>();
        foreach (var seg in session.Segments.OrderBy(s => s.StartUtc))
        {
            var rawEnd = seg.EndUtc ?? endUtc;
            var end = rawEnd;
            var startUtc = seg.StartUtc;
            if (seg.Kind == StationKind.Pc)
            {
                end = SessionPrepaidRecalculator.TrimPcSegmentEnd(seg, rawEnd, endUtc);
                startUtc = SessionPrepaidRecalculator.TrimPcSegmentStart(seg, end, endUtc);
                if (end <= startUtc)
                {
                    continue;
                }
            }

            string? gameDisplayName = null;
            if (seg.GameTitleId is int gameId)
            {
                gameDisplayName = seg.GameTitle?.Name;
                if (gameDisplayName is null)
                {
                    gameDisplayName = db.GameTitles.AsNoTracking()
                        .Where(g => g.Id == gameId)
                        .Select(g => g.Name)
                        .FirstOrDefault();
                }
            }

            segments.Add(new PlaySegment(
                seg.SegmentKey,
                TimeMapping.ToTehran(startUtc),
                TimeMapping.ToTehran(end),
                seg.StationId,
                seg.GroupId,
                seg.Mode,
                seg.PlayerCount,
                seg.Kind,
                seg.GameTitleId,
                seg.GameHourlyRateToman,
                seg.GameNightHourlyRateToman,
                seg.SplitReason,
                gameDisplayName));
        }

        IReadOnlyList<PauseInterval>? pauses = null;
        if (session.Pauses.Count > 0)
        {
            pauses = session.Pauses
                .Select(p => new PauseInterval(
                    TimeMapping.ToTehran(p.StartUtc),
                    TimeMapping.ToTehran(p.EndUtc ?? endUtc)))
                .ToList();
        }

        var billingSession = new Session(session.Id.ToString(), segments, pauses);
        return SessionPricer.Price(billingSession, snapshot.Card, stations: directory);
    }

    private StationDirectory BuildDirectory()
    {
        var groups = db.StationGroups.AsNoTracking().AsEnumerable()
            .Select(g => new StationGroup(g.Id, g.Name, g.Kind));
        var stations = db.Stations.AsNoTracking().AsEnumerable()
            .Select(s => new Station(s.Id, s.Name, s.GroupId));
        return new StationDirectory(groups, stations);
    }
}

public sealed class SessionCloser(
    AppDbContext db,
    IClock clock,
    SessionBilling billing,
    OperatorMemberBillingService memberBilling,
    Operator.OperatorShiftGate shiftGate,
    Operator.OperatorContext operatorContext)
{
    public async Task<InvoiceEntity> FinalizeFromPendingAsync(
        PlaySessionEntity session,
        PendingBillEntity bill,
        IReadOnlyList<OperatorBillService.PaymentInput> payments,
        BillAmounts amounts,
        CancellationToken ct)
    {
        await shiftGate.EnsureOpenShiftForMoneyAsync(ct);
        var shiftId = await shiftGate.RequireOpenShiftIdAsync(ct);
        var openSale = await db.OpenSales.Include(o => o.Lines).ThenInclude(l => l.CatalogItem)
            .FirstOrDefaultAsync(o => o.SessionId == session.Id, ct);
        var chargeForLines = await billing.BuildChargeAsync(session, bill.FrozenAtUtc, ct);
        var invoice = await CreateInvoiceAsync(
            amounts,
            bill,
            payments,
            session.Id,
            openSale?.Id,
            session.CustomerId,
            shiftId,
            chargeForLines,
            ct);
        if (openSale is not null)
        {
            CopyCatalogLines(invoice, openSale.Lines);
            db.OpenSaleLines.RemoveRange(openSale.Lines);
            db.OpenSales.Remove(openSale);
        }

        session.Status = SessionStatus.Closed;
        session.Invoice = invoice;
        db.Invoices.Add(invoice);
        db.PendingBills.Remove(bill);
        session.PendingBill = null;
        if (session.CustomerId is Guid customerId && amounts.Detail is not null)
        {
            var member = await db.CustomerMembers.FirstOrDefaultAsync(m => m.CustomerId == customerId, ct);
            if (member is null)
            {
                member = new CustomerMemberEntity { CustomerId = customerId };
                db.CustomerMembers.Add(member);
            }

            if (amounts.Detail.Allocation is { } allocation)
            {
                await memberBilling.ApplyWalletPaymentsAsync(customerId, member, allocation, invoice.Id, ct);
            }

            var charge = await billing.BuildChargeAsync(session, bill.FrozenAtUtc, ct);
            await memberBilling.PostConfirmEffectsAsync(customerId, member, amounts.Detail, invoice.Id, charge.TotalBilledMinutes, ct);
        }

        await db.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task<InvoiceEntity> FinalizeBuffetOnlyAsync(
        OpenSaleEntity sale,
        PendingBillEntity bill,
        IReadOnlyList<OperatorBillService.PaymentInput> payments,
        BillAmounts amounts,
        CancellationToken ct)
    {
        await shiftGate.EnsureOpenShiftForMoneyAsync(ct);
        var shiftId = await shiftGate.RequireOpenShiftIdAsync(ct);
        var invoice = await CreateInvoiceAsync(amounts, bill, payments, null, sale.Id, sale.CustomerId, shiftId, null, ct);
        CopyCatalogLines(invoice, sale.Lines);
        db.OpenSaleLines.RemoveRange(sale.Lines);
        db.OpenSales.Remove(sale);
        db.Invoices.Add(invoice);
        db.PendingBills.Remove(bill);
        if (sale.CustomerId is Guid customerId && amounts.Detail is not null)
        {
            var member = await db.CustomerMembers.FirstOrDefaultAsync(m => m.CustomerId == customerId, ct)
                ?? new CustomerMemberEntity { CustomerId = customerId };
            if (!await db.CustomerMembers.AnyAsync(m => m.CustomerId == customerId, ct))
            {
                db.CustomerMembers.Add(member);
            }

            if (amounts.Detail.Allocation is { } allocation)
            {
                await memberBilling.ApplyWalletPaymentsAsync(customerId, member, allocation, invoice.Id, ct);
            }

            await memberBilling.PostConfirmEffectsAsync(customerId, member, amounts.Detail, invoice.Id, 0, ct);
        }

        await db.SaveChangesAsync(ct);
        return invoice;
    }

    private async Task<InvoiceEntity> CreateInvoiceAsync(
        BillAmounts amounts,
        PendingBillEntity bill,
        IReadOnlyList<OperatorBillService.PaymentInput> payments,
        Guid? sessionId,
        Guid? openSaleId,
        Guid? customerId,
        Guid? shiftId,
        SessionCharge? sessionCharge,
        CancellationToken ct)
    {
        var number = await NextInvoiceNumberAsync(ct);
        var writeOffToman = amounts.Detail?.PlaySettlement?.WriteOffToman ?? 0;
        if (writeOffToman == 0)
        {
            writeOffToman = amounts.Detail?.InvoicePreview.Lines
                .Where(l => l.Kind == InvoiceLineKind.WriteOff)
                .Sum(l => -l.AmountToman) ?? 0L;
        }
        var invoice = new InvoiceEntity
        {
            Id = Guid.NewGuid(),
            Number = number,
            SessionId = sessionId,
            OpenSaleId = openSaleId,
            ShiftId = shiftId,
            SettledByUserId = operatorContext.UserId,
            TimeGrossToman = amounts.TimeGrossToman,
            BuffetGrossToman = amounts.BuffetGrossToman,
            ServicesGrossToman = amounts.ServicesGrossToman,
            UnroundedPayableToman = amounts.UnroundedPayableToman,
            RoundingToman = amounts.RoundingToman,
            CollectibleToman = amounts.CollectibleToman,
            SurchargeToman = amounts.SurchargeToman,
            WriteOffToman = writeOffToman,
            DiscountToman = amounts.DiscountToman,
            LevelDiscountToman = SplitMemberDiscounts(amounts.Detail).Level,
            BirthdayDiscountToman = SplitMemberDiscounts(amounts.Detail).Birthday,
            BilledToUtc = bill.FrozenAtUtc,
            IssuedAtUtc = clock.UtcNow.UtcDateTime,
            Status = "confirmed",
            StopReason = bill.StopReason,
            CustomerId = customerId,
        };
        var allocTenders = amounts.Detail?.Allocation?.Tenders;
        var counterLineAmounts = ResolveCounterPaymentLineAmounts(payments, amounts.CollectibleToman, amounts.Detail?.Allocation);
        for (var paymentIndex = 0; paymentIndex < payments.Count; paymentIndex++)
        {
            var pay = payments[paymentIndex];
            var method = OperatorPaymentMethods.Parse(pay.Method);
            if (pay.AmountToman <= 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "مبلغ هر خط پرداخت باید بیشتر از صفر باشد.");
            }

            var allocTender = method == PaymentMethod.Wallet
                ? allocTenders?.FirstOrDefault(t => t.Method == PaymentMethod.Wallet && t.Accepted)
                : null;
            var lineAmount = counterLineAmounts[paymentIndex];

            if (method == PaymentMethod.Wallet && allocTender is not null)
            {
                var drawn = MoneyMath.CheckedAdd(allocTender.RealDrawnToman, allocTender.GiftDrawnToman);
                if (drawn > lineAmount)
                {
                    lineAmount = drawn;
                }
                else if (allocTender.AmountToman > lineAmount)
                {
                    lineAmount = allocTender.AmountToman;
                }
            }

            var walletDraw = allocTender;
            invoice.PaymentLines.Add(new PaymentLineEntity
            {
                Method = method,
                AmountToman = lineAmount,
                DestinationCardId = pay.DestinationCardId,
                Reference = pay.Reference,
                ConfirmationStatus = method == PaymentMethod.CardToCard
                    ? PaymentConfirmationStatus.Pending
                    : PaymentConfirmationStatus.Settled,
                WalletCustomerId = method == PaymentMethod.Wallet ? customerId : null,
                WalletRealDrawnToman = method == PaymentMethod.Wallet ? walletDraw?.RealDrawnToman ?? 0 : 0,
                WalletGiftDrawnToman = method == PaymentMethod.Wallet ? walletDraw?.GiftDrawnToman ?? 0 : 0,
            });
        }

        if (sessionCharge is not null)
        {
            foreach (var line in sessionCharge.Lines.Where(l => l.AmountToman > 0))
            {
                invoice.TimeLines.Add(Operator.Ss14BillDisplay.ToInvoiceTimeLine(invoice.Id, line));
            }
        }

        return invoice;
    }

    private static long[] ResolveCounterPaymentLineAmounts(
        IReadOnlyList<OperatorBillService.PaymentInput> payments,
        long collectibleToman,
        AllocationResult? allocation)
    {
        var recorded = new long[payments.Count];
        for (var i = 0; i < payments.Count; i++)
        {
            recorded[i] = payments[i].AmountToman;
        }

        if (payments.Sum(p => p.AmountToman) <= collectibleToman)
        {
            return recorded;
        }

        long walletRecorded = 0;
        for (var i = 0; i < payments.Count; i++)
        {
            var method = OperatorPaymentMethods.Parse(payments[i].Method);
            if (method is not (PaymentMethod.Wallet or PaymentMethod.PcBalance))
            {
                continue;
            }

            var allocTender = allocation?.Tenders.FirstOrDefault(t => t.Method == method && t.Accepted);
            var drawn = allocTender is null
                ? payments[i].AmountToman
                : Math.Max(
                    payments[i].AmountToman,
                    MoneyMath.CheckedAdd(allocTender.RealDrawnToman, allocTender.GiftDrawnToman));
            if (allocTender is not null && allocTender.AmountToman > drawn)
            {
                drawn = allocTender.AmountToman;
            }

            recorded[i] = drawn;
            walletRecorded = MoneyMath.CheckedAdd(walletRecorded, drawn);
        }

        var counterOwed = collectibleToman - walletRecorded;
        if (counterOwed < 0)
        {
            counterOwed = 0;
        }

        long nonCashRecorded = 0;
        var cashIndices = new List<int>();
        for (var i = 0; i < payments.Count; i++)
        {
            var method = OperatorPaymentMethods.Parse(payments[i].Method);
            if (method is PaymentMethod.Wallet or PaymentMethod.PcBalance)
            {
                continue;
            }

            if (method is PaymentMethod.Cash)
            {
                cashIndices.Add(i);
                continue;
            }

            var owedAfterPrior = counterOwed - nonCashRecorded;
            if (owedAfterPrior < 0)
            {
                owedAfterPrior = 0;
            }

            var take = Math.Min(payments[i].AmountToman, owedAfterPrior);
            recorded[i] = take;
            nonCashRecorded = MoneyMath.CheckedAdd(nonCashRecorded, take);
        }

        var cashOwed = counterOwed - nonCashRecorded;
        if (cashOwed < 0)
        {
            cashOwed = 0;
        }

        if (cashIndices.Count == 0)
        {
            return recorded;
        }

        if (cashIndices.Count == 1)
        {
            recorded[cashIndices[0]] = cashOwed;
            return recorded;
        }

        var lastCash = cashIndices[^1];
        for (var c = 0; c < cashIndices.Count - 1; c++)
        {
            var idx = cashIndices[c];
            var take = Math.Min(payments[idx].AmountToman, cashOwed);
            recorded[idx] = take;
            cashOwed -= take;
        }

        recorded[lastCash] = cashOwed;
        return recorded;
    }

    private static (long Level, long Birthday) SplitMemberDiscounts(MemberBillResult? detail)
    {
        if (detail?.InvoicePreview is not { } preview)
        {
            return (0, 0);
        }

        long level = 0;
        long birthday = 0;
        foreach (var line in preview.Lines.Where(l => l.Kind == InvoiceLineKind.Discount))
        {
            var amount = -line.AmountToman;
            if (line.Description.Contains("Birthday", StringComparison.OrdinalIgnoreCase))
            {
                birthday = MoneyMath.CheckedAdd(birthday, amount);
            }
            else
            {
                level = MoneyMath.CheckedAdd(level, amount);
            }
        }

        return (level, birthday);
    }

    private static void CopyCatalogLines(InvoiceEntity invoice, IEnumerable<OpenSaleLineEntity> lines)
    {
        foreach (var line in lines)
        {
            invoice.CatalogLines.Add(new InvoiceCatalogLineEntity
            {
                CatalogItemId = line.CatalogItemId,
                Name = line.CatalogItem?.Name ?? line.CatalogItemId,
                Kind = line.Kind,
                Quantity = line.Quantity,
                UnitPriceToman = line.UnitPriceToman,
                LineTotalToman = line.LineTotalToman,
            });
        }
    }

    public Task<InvoiceEntity> CloseAsync(PlaySessionEntity session, DateTime endUtc, string auditReason, CancellationToken ct)
        => throw new InvalidOperationException("Sessions must close through operator bill confirmation with payment lines.");

    private async Task<long> NextInvoiceNumberAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT nextval('invoice_number_seq')";
            var value = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(value);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}

public sealed class SessionService(
    AppDbContext db,
    IClock clock,
    AuditService audit,
    SessionBilling billing,
    RateConfigService rates,
    GameTariffService gameTariff,
    OperatorBillService operatorBills,
    Operator.OperatorShiftGate shiftGate,
    StationInternetStateService internetStates,
    IOperatorPanelNotifier panelNotifier)
{
    public Task<SessionGameChangeResult> ChangeGameAsync(
        Guid sessionId,
        int? gameTitleId,
        uint? expectedVersion,
        Guid commandId,
        CancellationToken ct)
        => SessionGameSupport.ChangeGameAsync(
            db,
            clock,
            audit,
            billing,
            rates,
            gameTariff,
            panelNotifier,
            sessionId,
            gameTitleId,
            expectedVersion,
            commandId,
            ct);
    public Task<SessionTransferResult> TransferAsync(
        Guid sessionId,
        string targetStationId,
        bool markSourceBroken,
        uint? expectedVersion,
        Guid commandId,
        CancellationToken ct)
        => SessionTransferSupport.TransferAsync(
            db,
            clock,
            audit,
            billing,
            rates,
            internetStates,
            panelNotifier,
            sessionId,
            targetStationId,
            markSourceBroken,
            expectedVersion,
            commandId,
            ct);

    public Task<SessionInternetChangeResult> ChangeInternetAsync(
        Guid sessionId,
        bool on,
        uint? expectedVersion,
        Guid commandId,
        CancellationToken ct)
        => SessionInternetSupport.ChangeInternetAsync(
            db,
            clock,
            audit,
            billing,
            rates,
            internetStates,
            panelNotifier,
            sessionId,
            on,
            expectedVersion,
            commandId,
            ct);

    public async Task<PlaySessionEntity> StartAsync(
        string stationId,
        int playerCount,
        PlayMode mode,
        DateTimeOffset? startedAt,
        int? gameTitleId,
        CancellationToken ct)
    {
        if (startedAt.HasValue)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "client_start_time",
                "زمان شروع جلسه را سرور تعیین می‌کند؛ startedAt نفرستید.");
        }

        await shiftGate.EnsureMoneyRecordingAllowedAsync(ct);
        var openShiftId = await shiftGate.RequireOpenShiftIdAsync(ct);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var normalizedId = StationIdNormalizer.Normalize(stationId);
            await db.Database.ExecuteSqlRawAsync(
                """SELECT 1 FROM stations WHERE "Id" = {0} FOR UPDATE""",
                normalizedId);
            var station = await db.Stations.Include(s => s.Group).FirstOrDefaultAsync(s => s.Id == normalizedId, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "ایستگاه یافت نشد.");
            if (station.Group is null || string.IsNullOrWhiteSpace(station.GroupId))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "no_group", "ایستگاه گروه ندارد.");
            }

            if (!station.IsActive)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "inactive", "ایستگاه غیرفعال است.");
            }

            if (station.OperationalState != StationOperationalState.Available)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "target_unavailable", "ایستگاه جلسهٔ جدید نمی‌پذیرد.");
            }

            if (station.Group.Kind == StationKind.Pc)
            {
                var kioskProfile = await db.StationKioskProfiles.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.StationId == station.Id, ct);
                if (kioskProfile?.IsTestMode == true)
                {
                    throw new ApiException(StatusCodes.Status409Conflict, "test_mode", "ایستگاه در حالت آزمایش است.");
                }
            }

            if (await db.Sessions.AnyAsync(
                    s => s.StationId == station.Id
                        && (s.Status == SessionStatus.Open
                            || s.Status == SessionStatus.Paused
                            || s.Status == SessionStatus.BillOpen
                            || s.Status == SessionStatus.AwaitingPayment),
                    ct))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "ایستگاه از قبل جلسهٔ باز دارد.");
            }

            if (playerCount is < 1 or > 4)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_players", "تعداد بازیکن باید بین ۱ تا ۴ باشد.");
            }

            if (station.Group.Kind == StationKind.Pc)
            {
                mode = PlayMode.Online;
                playerCount = 1;
            }

            var startUtc = clock.UtcNow.UtcDateTime;
            var rateSnapshot = await rates.CreateSnapshotEntityAsync(ct);

            var session = new PlaySessionEntity
            {
                Id = Guid.NewGuid(),
                StationId = station.Id,
                Status = SessionStatus.Open,
                StartedAtUtc = startUtc,
                Mode = mode,
                PlayerCount = playerCount,
                LastCheckpointUtc = station.Group.Kind == StationKind.Pc ? startUtc : null,
                RateCardSnapshot = rateSnapshot,
                OpenShiftId = openShiftId,
            };

            var firstSegment = new PlaySegmentEntity
            {
                SegmentKey = "a",
                StartUtc = startUtc,
                StationId = station.Id,
                GroupId = station.GroupId,
                Mode = mode,
                PlayerCount = playerCount,
                Kind = station.Group.Kind,
            };

            if (gameTitleId is not null)
            {
                if (station.Group.Kind != StationKind.Console)
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "not_console", "انتخاب بازی فقط برای کنسول است.");
                }

                var title = await gameTariff.EnsureGameForSessionAsync(gameTitleId, mode, playerCount, ct);
                var (hourly, night) = await gameTariff.ResolveSnapshotsAsync(gameTitleId.Value, mode, playerCount, ct);
                gameTariff.ApplySnapshots(firstSegment, gameTitleId.Value, hourly, night);
                session.CurrentGameTitleId = gameTitleId;
                _ = title;
            }

            session.Segments.Add(firstSegment);

            db.Sessions.Add(session);
            if (station.Group.Kind == StationKind.Pc)
            {
                await internetStates.BumpDesiredAsync(
                    station.Id,
                    desiredOn: true,
                    startUtc,
                    userId: null,
                    "session_start",
                    session.Id,
                    ct);
            }

            try
            {
                await audit.AppendAsync("session.start", new { sessionId = session.Id, stationId = station.Id, playerCount, mode = mode.ToString() }, ct);
            }
            catch (DbUpdateException ex) when (PostgresErrors.IsActiveSessionUniqueViolation(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "ایستگاه از قبل جلسهٔ باز دارد.");
            }

            await tx.CommitAsync(ct);
            return session;
        });
    }

    public Task PauseAsync(Guid sessionId, CancellationToken ct)
        => ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Station!.Group!.Kind == StationKind.Pc)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "pc_pause", "توقف برای ایستگاه کامپیوتر مجاز نیست.");
            }

            if (session.Status == SessionStatus.ManualSettle)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "جلسه در انتظار تسویهٔ دستی است.");
            }

            if (session.Status is SessionStatus.BillOpen or SessionStatus.AwaitingPayment)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "awaiting_payment", "جلسه از قبل صورتحساب باز دارد.");
            }

            if (session.Status == SessionStatus.Paused)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "already_paused", "جلسه از قبل متوقف شده است.");
            }

            var now = clock.UtcNow.UtcDateTime;
            session.Status = SessionStatus.Paused;
            session.Pauses.Add(new PauseIntervalEntity { StartUtc = now });
            try
            {
                await audit.AppendAsync("session.pause", new { sessionId }, innerCt);
            }
            catch (DbUpdateException ex) when (PostgresErrors.IsOpenPauseUniqueViolation(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "already_paused", "جلسه از قبل متوقف شده است.");
            }
        }, ct);

    public Task ResumeAsync(Guid sessionId, CancellationToken ct)
        => ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Status == SessionStatus.ManualSettle)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "جلسه در انتظار تسویهٔ دستی است.");
            }

            if (session.Status is SessionStatus.BillOpen or SessionStatus.AwaitingPayment)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "awaiting_payment", "جلسه از قبل صورتحساب باز دارد.");
            }

            if (session.Status != SessionStatus.Paused)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "not_paused", "جلسه متوقف نیست.");
            }

            var openPause = session.Pauses.LastOrDefault(p => p.EndUtc is null)
                ?? throw new ApiException(StatusCodes.Status409Conflict, "not_paused", "توقفی باز نیست.");
            openPause.EndUtc = clock.UtcNow.UtcDateTime;
            session.Status = SessionStatus.Open;
            await audit.AppendAsync("session.resume", new { sessionId }, innerCt);
        }, ct);

    public Task<object> StopAsync(Guid sessionId, CancellationToken ct)
        => StopAsync(sessionId, null, ct);

    public Task<object> StopAsync(Guid sessionId, DateTime? pcEndUtc, CancellationToken ct)
        => ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Status == SessionStatus.ManualSettle)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "جلسه در انتظار تسویهٔ دستی است.");
            }

            if (session.Status is SessionStatus.AwaitingPayment or SessionStatus.BillOpen)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "awaiting_payment", "جلسه از قبل صورتحساب باز دارد.");
            }

            if (session.Station!.Group!.Kind == StationKind.Pc)
            {
                var endUtc = pcEndUtc ?? session.LastCheckpointUtc ?? session.StartedAtUtc;
                var bill = await operatorBills.CreatePcPendingBillAsync(session, endUtc, "pc_stop", "logout", innerCt);
                await audit.AppendAsync("session.stop", new { sessionId, reason = "stop", pendingBillId = bill.Id }, innerCt);
                return PendingStopBody(bill);
            }

            var consoleEnd = clock.UtcNow.UtcDateTime;
            var consoleBill = await operatorBills.CreateMachineStopPendingBillAsync(session, consoleEnd, innerCt);
            await audit.AppendAsync("session.stop", new { sessionId, reason = "stop", pendingBillId = consoleBill.Id }, innerCt);
            return PendingStopBody(consoleBill);
        }, ct);

    public Task RecordCheckpointAsync(Guid sessionId, DateTimeOffset? at, CancellationToken ct)
        => ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Station!.Group!.Kind != StationKind.Pc)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "not_pc", "نقطهٔ کنترل فقط برای جلسهٔ کامپیوتر است.");
            }

            if (session.Status is not SessionStatus.Open and not SessionStatus.Paused)
            {
                if (session.Status == SessionStatus.ManualSettle)
                {
                    throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "جلسه در انتظار تسویهٔ دستی است.");
                }

                throw new ApiException(StatusCodes.Status409Conflict, "closed", "جلسه بسته شده است.");
            }

            var serverNow = clock.UtcNow.UtcDateTime;
            var previous = session.LastCheckpointUtc ?? session.StartedAtUtc;
            DateTime? reportedUtc = null;

            if (at.HasValue)
            {
                reportedUtc = TimeMapping.ToUtc(TehranTime.Ensure(at.Value));
                if (reportedUtc > serverNow.AddSeconds(60))
                {
                    throw new ApiException(
                        StatusCodes.Status400BadRequest,
                        "checkpoint_in_future",
                        "زمان نقطهٔ کنترل نمی‌تواند بیشتر از ۶۰ ثانیه جلوتر از زمان سرور باشد.");
                }

                if (reportedUtc < previous)
                {
                    session.Status = SessionStatus.ManualSettle;
                    session.ManualSettleReason = "rejected_checkpoint";
                    await audit.AppendAsync("session.manual_settle", new { sessionId, reason = "rejected_checkpoint" }, innerCt);
                    await db.SaveChangesAsync(innerCt);
                    return;
                }
            }

            DateTime acceptedUtc;
            if (reportedUtc is null)
            {
                acceptedUtc = serverNow;
            }
            else
            {
                var minReportServer = reportedUtc.Value <= serverNow ? reportedUtc.Value : serverNow;
                acceptedUtc = previous >= minReportServer ? previous : minReportServer;
            }

            session.LastCheckpointUtc = acceptedUtc;
            session.Checkpoints.Add(new CheckpointEntity { AtUtc = acceptedUtc });
            await audit.AppendAsync("session.checkpoint", new { sessionId, atUtc = acceptedUtc }, innerCt);
            await db.SaveChangesAsync(innerCt);
        }, ct);

    public Task RecordAcceptedCheckpointsAsync(Guid sessionId, IReadOnlyList<DateTime> acceptedUtc, CancellationToken ct)
        => ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Station!.Group!.Kind != StationKind.Pc)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "not_pc", "نقطهٔ کنترل فقط برای جلسهٔ کامپیوتر است.");
            }

            if (session.Status is not SessionStatus.Open and not SessionStatus.Paused)
            {
                if (session.Status == SessionStatus.ManualSettle)
                {
                    throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "جلسه در انتظار تسویهٔ دستی است.");
                }

                throw new ApiException(StatusCodes.Status409Conflict, "closed", "جلسه بسته شده است.");
            }

            if (acceptedUtc.Count == 0)
            {
                return;
            }

            foreach (var at in acceptedUtc)
            {
                var previous = session.LastCheckpointUtc ?? session.StartedAtUtc;
                if (at < previous)
                {
                    session.Status = SessionStatus.ManualSettle;
                    session.ManualSettleReason = "rejected_checkpoint";
                    await audit.AppendAsync("session.manual_settle", new { sessionId, reason = "rejected_checkpoint" }, innerCt);
                    await db.SaveChangesAsync(innerCt);
                    return;
                }

                session.LastCheckpointUtc = at;
                session.Checkpoints.Add(new CheckpointEntity { AtUtc = at });
                await audit.AppendAsync("session.checkpoint", new { sessionId, atUtc = at }, innerCt);
            }

            await db.SaveChangesAsync(innerCt);
        }, ct);

    public Task ChangePlayerCountAsync(Guid sessionId, int playerCount, CancellationToken ct)
        => ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Station!.Group!.Kind != StationKind.Console)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "not_console", "تغییر تعداد دسته فقط برای کنسول است.");
            }

            if (session.Status == SessionStatus.ManualSettle)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "جلسه در انتظار تسویهٔ دستی است.");
            }

            if (session.Status is SessionStatus.BillOpen or SessionStatus.AwaitingPayment)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "awaiting_payment", "جلسه از قبل صورتحساب باز دارد.");
            }

            if (playerCount is < 1 or > 4)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_players", "تعداد بازیکن باید بین ۱ تا ۴ باشد.");
            }

            if (session.PlayerCount == playerCount)
            {
                return;
            }

            var now = clock.UtcNow.UtcDateTime;
            var openSegment = session.Segments.SingleOrDefault(s => s.EndUtc is null)
                ?? throw new ApiException(StatusCodes.Status409Conflict, "awaiting_payment", "جلسه از قبل صورتحساب باز دارد.");
            if (session.CurrentGameTitleId is int gameId)
            {
                _ = await gameTariff.ResolveSnapshotsAsync(gameId, session.Mode, playerCount, innerCt);
            }

            openSegment.EndUtc = now;
            var nextKey = SegmentKeySupport.Allocate(session.Segments.Count);
            var newSegment = new PlaySegmentEntity
            {
                SegmentKey = nextKey,
                StartUtc = now,
                StationId = session.StationId,
                GroupId = session.Station.GroupId,
                Mode = session.Mode,
                PlayerCount = playerCount,
                Kind = StationKind.Console,
            };
            if (session.CurrentGameTitleId is int activeGame)
            {
                var (hourly, night) = await gameTariff.ResolveSnapshotsAsync(activeGame, session.Mode, playerCount, innerCt);
                gameTariff.ApplySnapshots(newSegment, activeGame, hourly, night);
            }

            session.Segments.Add(newSegment);
            session.PlayerCount = playerCount;

            var snapshot = await rates.LoadSnapshotAsync(session.RateCardSnapshotId, innerCt);
            SessionPrepaidRecalculator.RecalcAmountPrepaid(session, newSegment, snapshot, now);
            await audit.AppendAsync("session.player_count", new { sessionId, playerCount }, innerCt);
        }, ct);

    public async Task<long> LiveAmountAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await LoadOpenSession(sessionId, ct);
        return await LiveAmountAsync(session, ct);
    }

    public async Task<long> LiveAmountAsync(PlaySessionEntity session, CancellationToken ct)
    {
        if (session.Status == SessionStatus.ManualSettle)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "جلسه در انتظار تسویهٔ دستی است.");
        }

        if (session.Status is SessionStatus.BillOpen or SessionStatus.AwaitingPayment)
        {
            var pending = session.PendingBill
                ?? await db.PendingBills.AsNoTracking().FirstOrDefaultAsync(b => b.SessionId == session.Id, ct);
            if (pending is not null)
            {
                return pending.CollectibleToman;
            }
        }

        var endUtc = clock.UtcNow.UtcDateTime;
        var charge = await billing.BuildChargeAsync(session, endUtc, ct);
        return charge.TotalAmountToman;
    }

    public async Task<PendingBillEntity> ResolveManualSettleAsync(Guid sessionId, DateTimeOffset? endAt, CancellationToken ct)
    {
        return await ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Status != SessionStatus.ManualSettle
                && !(session.Status == SessionStatus.AwaitingPayment && session.ManualSettleReason != null))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "not_manual_settle", "جلسه در انتظار تسویهٔ دستی نیست.");
            }

            var now = clock.UtcNow.UtcDateTime;
            var lastAccepted = session.LastCheckpointUtc ?? session.StartedAtUtc;
            DateTime endUtc;
            if (!endAt.HasValue)
            {
                endUtc = lastAccepted;
            }
            else
            {
                endUtc = TimeMapping.ToUtc(TehranTime.Ensure(endAt.Value));
                if (endUtc > now)
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "future_end", "زمان پایان نمی‌تواند در آینده باشد.");
                }

                if (endUtc < lastAccepted)
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "before_checkpoint", "زمان پایان باید از آخرین نقطهٔ کنترل پذیرفته‌شده به بعد باشد.");
                }
            }

            var reason = session.ManualSettleReason;
            PendingBillEntity pending;
            if (session.PendingBill is not null
                || await db.PendingBills.AnyAsync(b => b.SessionId == session.Id, innerCt))
            {
                pending = session.PendingBill
                    ?? await db.PendingBills.FirstAsync(b => b.SessionId == session.Id, innerCt);
            }
            else
            {
                pending = await operatorBills.CreatePcPendingBillAsync(session, endUtc, "manual_settle", reason, innerCt);
            }

            session.ManualSettleReason = null;
            await db.SaveChangesAsync(innerCt);
            await audit.AppendAsync("session.manual_settle_resolve", new { sessionId, endUtc, reason }, innerCt);
            return pending;
        }, ct);
    }

    private async Task<PlaySessionEntity> LoadOpenSession(Guid id, CancellationToken ct)
    {
        var session = await db.Sessions
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .Include(s => s.Segments)
            .Include(s => s.Pauses)
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "جلسه یافت نشد.");
        if (session.Status is SessionStatus.Closed)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "closed", "جلسه بسته شده است.");
        }

        return session;
    }

    private async Task ExecuteWithSessionWriteLockAsync(
        Guid sessionId,
        Func<PlaySessionEntity, CancellationToken, Task> action,
        CancellationToken ct)
    {
        _ = await ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            await action(session, innerCt);
            return false;
        }, ct);
    }

    private async Task LockSessionRowForUpdateAsync(Guid sessionId, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """SELECT "Id" FROM sessions WHERE "Id" = {0} FOR UPDATE NOWAIT""",
                sessionId);
        }
        catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "session_busy", "جلسه در حال به‌روزرسانی است.");
        }
    }

    private async Task<T> ExecuteWithSessionWriteLockAsync<T>(
        Guid sessionId,
        Func<PlaySessionEntity, CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockSessionRowForUpdateAsync(sessionId, ct);

            var session = await db.Sessions
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Include(s => s.Segments)
                .Include(s => s.Pauses)
                .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "جلسه یافت نشد.");
            if (session.Status == SessionStatus.Closed)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "closed", "جلسه بسته شده است.");
            }

            var result = await action(session, ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private static object PendingStopBody(PendingBillEntity bill) => new
    {
        pending = true,
        unroundedPayableToman = bill.UnroundedPayableToman,
        roundingToman = bill.RoundingToman,
        collectibleToman = bill.CollectibleToman,
    };
}

public interface IDeadPcMonitor
{
    Task<int> RunDueClosuresAsync(CancellationToken ct);
}

public sealed class DeadPcMonitor(IServiceScopeFactory scopes, IClock clock, ILogger<DeadPcMonitor> logger) : IDeadPcMonitor
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public async Task<int> RunDueClosuresAsync(CancellationToken ct)
    {
        var now = clock.UtcNow.UtcDateTime;
        List<(Guid Id, DateTime EndUtc, string StationId)> due;
        using (var listScope = scopes.CreateScope())
        {
            var listDb = listScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var candidates = await listDb.Sessions
                .AsNoTracking()
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Where(s => s.Status == SessionStatus.Open && s.LastCheckpointUtc != null)
                .ToListAsync(ct);
            due = candidates
                .Where(s => s.Station?.Group?.Kind == StationKind.Pc
                    && s.LastCheckpointUtc!.Value <= now - Timeout)
                .Select(s => (s.Id, s.LastCheckpointUtc!.Value, s.StationId))
                .ToList();
        }

        var closed = 0;
        foreach (var item in due)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var operatorBills = scope.ServiceProvider.GetRequiredService<OperatorBillService>();
                var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
                var strategy = db.Database.CreateExecutionStrategy();
                var closedThis = false;
                await strategy.ExecuteAsync(async () =>
                {
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    try
                    {
                        await db.Database.ExecuteSqlRawAsync(
                            """SELECT "Id" FROM sessions WHERE "Id" = {0} FOR UPDATE NOWAIT""",
                            item.Id);
                    }
                    catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex))
                    {
                        return;
                    }
                    var session = await db.Sessions
                        .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                        .Include(s => s.Segments)
                        .Include(s => s.Pauses)
                        .FirstAsync(s => s.Id == item.Id, ct);
                    if (session.Status != SessionStatus.Open)
                    {
                        return;
                    }

                    var lastAccepted = session.LastCheckpointUtc ?? session.StartedAtUtc;
                    if (lastAccepted > now - Timeout)
                    {
                        return;
                    }

                    await operatorBills.CreatePcPendingBillAsync(session, lastAccepted, "auto_close", "auto-close", ct);
                    db.AuditLog.Add(new AuditLogEntry
                    {
                        AtUtc = clock.UtcNow.UtcDateTime,
                        Action = "session.stop",
                        DetailsJson = JsonSerializer.Serialize(new { sessionId = session.Id, reason = "auto-close" }),
                    });
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    closedThis = true;
                });
                if (closedThis)
                {
                    closed++;
                }
            }
            catch (ApiException ex) when (ex.Code is "closed")
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dead-PC auto-close failed for session {SessionId} on station {StationId}", item.Id, item.StationId);
            }
        }

        return closed;
    }
}

public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

  public IReadOnlyDictionary<string, object>? Extras { get; init; }
}

public sealed class DatabaseSeed
{
    public static async Task EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (!await db.StationGroups.AnyAsync(ct))
        {
            db.StationGroups.AddRange(
                new StationGroupEntity { Id = PilotSeed.PcNormalGroupId, Name = "PC Normal", Kind = StationKind.Pc },
                new StationGroupEntity { Id = PilotSeed.PcVipGroupId, Name = "PC VIP", Kind = StationKind.Pc },
                new StationGroupEntity { Id = GameTimerReference.Ps5GroupId, Name = "PS5", Kind = StationKind.Console });
        }

        if (!await db.InvoiceNumbers.AnyAsync(ct))
        {
            db.InvoiceNumbers.Add(new InvoiceNumberSequence { Id = 1, NextNumber = 1 });
        }

        if (!await db.RateLines.AnyAsync(ct))
        {
            db.RateLines.AddRange(RateConfigService.PilotSeedLines());
        }

        if (!await db.BranchRounding.AnyAsync(ct))
        {
            db.BranchRounding.Add(new BranchRoundingEntity
            {
                Id = 1,
                StepToman = 5_000,
                Direction = "Up",
                RoundingEnabled = true,
                ApplyToWallet = true,
                WalletMinBillToman = WalletPaymentRounding.CashLikeThresholdToman,
                AllowWalletShortBalanceDebt = true,
            });
        }

        await db.SaveChangesAsync(ct);

        try
        {
            if (!await db.Stations.AnyAsync(s => s.Id == BuffetSaleConstants.VirtualStationId, ct))
            {
                if (!await db.StationGroups.AnyAsync(g => g.Id == BuffetSaleConstants.VirtualGroupId, ct))
                {
                    db.StationGroups.Add(new StationGroupEntity
                    {
                        Id = BuffetSaleConstants.VirtualGroupId,
                        Name = "Buffet only",
                        Kind = StationKind.TimedTable,
                    });
                }

                db.Stations.Add(new StationEntity
                {
                    Id = BuffetSaleConstants.VirtualStationId,
                    Name = "بوفه",
                    GroupId = BuffetSaleConstants.VirtualGroupId,
                    DefaultPlayerCount = 1,
                    DefaultPlayMode = PlayMode.Offline,
                    IsActive = true,
                });
            }

            CatalogSeed.Ensure(db);
            await db.SaveChangesAsync(ct);
        }
        catch (PostgresException ex) when (ex.SqlState is "42P01" or "42703")
        {
        }

        try
        {
            if (!await db.BranchMemberSettings.AnyAsync(ct))
            {
                db.BranchMemberSettings.Add(new BranchMemberSettingsEntity
                {
                    LevelLadderJson = MemberSettingsMapper.SerializeLadder(LevelLadder.Default),
                    BirthdayPercent = 10,
                    BirthdayWindowKind = "birth_month",
                });
                await db.SaveChangesAsync(ct);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
        }

        try
        {
            if (!await db.BranchCafeSettings.AnyAsync(ct))
            {
                db.BranchCafeSettings.Add(new BranchCafeSettingsEntity());
                await db.SaveChangesAsync(ct);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
        }

        try
        {
            if (!await db.OperatorUsers.AnyAsync(ct))
            {
                var manager = new OperatorUserEntity { Id = Guid.NewGuid(), Username = "manager", Role = UserRole.Manager };
                manager.PinHash = OperatorAuthService.HashPin(manager, "1234");
                var op = new OperatorUserEntity { Id = Guid.NewGuid(), Username = "operator", Role = UserRole.Operator };
                op.PinHash = OperatorAuthService.HashPin(op, "1111");
                db.OperatorUsers.AddRange(manager, op);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
        }

        try
        {
            if (!await db.GameCatalogCategories.AnyAsync(ct))
            {
                db.GameCatalogCategories.Add(new GameCatalogCategoryEntity { Id = 1, Name = "عمومی", SortOrder = 1 });
                await db.SaveChangesAsync(ct);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
        }
    }
}
