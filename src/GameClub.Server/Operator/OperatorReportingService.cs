using System.Text;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public sealed class OperatorReportingService(AppDbContext db, OperatorContext op)
{
    public async Task<object> DailyIncomeAsync(int year, int month, int day, CancellationToken ct)
    {
        EnsureReportRole();
        return await BuildIncomeForWorkingDayAsync(year, month, day, ct);
    }

    public async Task<object> MonthlyIncomeAsync(int jalaliYear, int jalaliMonth, CancellationToken ct)
    {
        EnsureReportRole();
        var days = await ListWorkingDaysInJalaliMonthAsync(jalaliYear, jalaliMonth, ct);
        var rows = new List<object>();
        foreach (var day in days)
        {
            var income = await BuildIncomeForWorkingDayAsync(day.Year, day.Month, day.Day, ct);
            rows.Add(income);
        }

        return RollUpIncomeRows(rows, new { jalaliYear, jalaliMonth });
    }

    public async Task<object> YearlyIncomeAsync(int jalaliYear, CancellationToken ct)
    {
        EnsureReportRole();
        var monthRows = new List<object>();
        for (var m = 1; m <= 12; m++)
        {
            var monthly = await MonthlyIncomeAsync(jalaliYear, m, ct);
            monthRows.Add(monthly);
        }

        return RollUpIncomeRows(monthRows, new { jalaliYear });
    }

    public async Task<object> HistoryAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? stationId,
        Guid? customerId,
        Guid? operatorId,
        string? method,
        CancellationToken ct)
    {
        EnsureReportRole();
        var q = db.Invoices.AsNoTracking()
            .Include(i => i.PaymentLines)
            .Include(i => i.Session)!.ThenInclude(s => s!.Station)
            .AsQueryable();

        if (fromUtc is DateTime from)
        {
            var fromTehran = TimeMapping.ToTehran(from);
            var fromDay = new LocalDate(fromTehran.Year, fromTehran.Month, fromTehran.Day);
            var fromBound = TimeMapping.ToUtc(fromTehran.Date);
            q = q.Where(i => i.IssuedAtUtc >= fromBound || (i.WorkingDayYear == fromDay.Year && i.WorkingDayMonth == fromDay.Month && i.WorkingDayDay == fromDay.Day));
        }

        if (toUtc is DateTime to)
        {
            var toTehran = TimeMapping.ToTehran(to);
            var toDay = new LocalDate(toTehran.Year, toTehran.Month, toTehran.Day);
            var nextDay = toDay.AddDays(1);
            var toExclusive = TimeMapping.ToUtc(TehranTime.At(nextDay.Year, nextDay.Month, nextDay.Day, 0, 0));
            q = q.Where(i => i.IssuedAtUtc < toExclusive);
        }

        if (customerId is Guid cid)
        {
            q = q.Where(i => i.CustomerId == cid);
        }

        if (operatorId is Guid oid)
        {
            q = q.Where(i => i.SettledByUserId == oid);
        }

        if (!string.IsNullOrWhiteSpace(stationId))
        {
            var sid = StationIdNormalizer.Normalize(stationId);
            q = q.Where(i => i.Session != null && i.Session.StationId == sid);
        }

        var rows = await q.OrderByDescending(i => i.IssuedAtUtc).Take(500).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(method))
        {
            var parsed = OperatorPaymentMethods.Parse(method);
            rows = rows.Where(i => i.PaymentLines.Any(p => p.Method == parsed)).ToList();
        }

        return rows.Select(i => new
        {
            i.Number,
            i.Status,
            i.CollectibleToman,
            stationId = i.Session?.StationId,
            i.SettledByUserId,
            workingDay = i.WorkingDayYear is null ? null : new { i.WorkingDayYear, i.WorkingDayMonth, i.WorkingDayDay },
            payments = i.PaymentLines.Select(p => new { method = p.Method.ToString(), p.AmountToman, status = p.ConfirmationStatus.ToString() }),
            i.OperatorNote,
            i.VoidReason,
        }).ToList();
    }

    public async Task<byte[]> HistoryExcelAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? stationId,
        Guid? customerId,
        Guid? operatorId,
        string? method,
        CancellationToken ct)
    {
        EnsureReportRole();
        var history = await HistoryAsync(fromUtc, toUtc, stationId, customerId, operatorId, method, ct);
        var rows = (IEnumerable<object>)history;
        var sb = new StringBuilder();
        sb.AppendLine("number,status,collectible,stationId,settledBy,workingDayYear,workingDayMonth,workingDayDay,payments,operatorNote,voidReason");
        foreach (dynamic row in rows)
        {
            var wd = row.workingDay;
            var payments = string.Join("|", ((IEnumerable<object>)row.payments).Select(p =>
            {
                dynamic d = p;
                return $"{d.method}:{d.AmountToman}:{d.status}";
            }));
            sb.AppendLine(string.Join(',',
                row.Number,
                row.Status,
                row.CollectibleToman,
                row.stationId,
                row.SettledByUserId,
                wd?.WorkingDayYear,
                wd?.WorkingDayMonth,
                wd?.WorkingDayDay,
                $"\"{payments}\"",
                $"\"{row.OperatorNote}\"",
                $"\"{row.VoidReason}\""));
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public async Task<object> UsageAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        EnsureReportRole();
        var sessions = await db.Sessions.AsNoTracking()
            .Include(s => s.Segments)
            .Include(s => s.Pauses)
            .Where(s => s.StartedAtUtc <= toUtc && (s.EndedAtUtc == null || s.EndedAtUtc >= fromUtc))
            .ToListAsync(ct);

        var (byStation, byHour) = OperatorUsageMath.Accumulate(sessions, fromUtc, toUtc);
        var peakHours = byHour.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(5)
            .Select(kv => new { hour = kv.Key, durationSeconds = kv.Value }).ToList();
        var rankedStations = byStation.OrderByDescending(kv => kv.Value)
            .Select(kv => new { stationId = kv.Key, durationSeconds = kv.Value }).ToList();
        return new { byStation = rankedStations, peakHours, writeOffs = Array.Empty<object>() };
    }

    public async Task<object> Cu8WalletReportsAsync(CancellationToken ct)
    {
        EnsureReportRole();
        var members = await db.CustomerMembers.AsNoTracking().ToListAsync(ct);
        var realLiability = members.Sum(m => Math.Max(0, m.RealToman));
        var giftLiability = members.Sum(m => Math.Max(0, m.GiftToman));
        var giftsIssued = await db.WalletLedger.AsNoTracking()
            .Where(w => w.Kind == WalletLedgerKind.TopUpGift)
            .SumAsync(w => w.GiftDeltaToman, ct);
        return new { realMoneyLiabilityToman = realLiability, giftCreditLiabilityToman = giftLiability, giftsIssuedToman = giftsIssued };
    }

    public async Task<object> CloseDayIncomeAsync(Guid closeRecordId, CancellationToken ct)
    {
        EnsureReportRole();
        var close = await db.CloseRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == closeRecordId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Close record not found.");
        var daily = await BuildIncomeForWorkingDayAsync(close.WorkingDayYear, close.WorkingDayMonth, close.WorkingDayDay, ct);
        var expected = await ComputeExpectedForCloseRecordAsync(close, ct);
        return new
        {
            closeRecordId,
            workingDay = new { close.WorkingDayYear, close.WorkingDayMonth, close.WorkingDayDay },
            actualCashToman = close.ActualCashToman,
            actualCardReaderToman = close.ActualCardReaderToman,
            expectedCashToman = expected.Cash,
            expectedCardReaderToman = expected.CardReader,
            expectedCardToCardToman = expected.CardToCardConfirmed,
            income = daily,
        };
    }

    private async Task<object> BuildIncomeForWorkingDayAsync(int year, int month, int day, CancellationToken ct)
    {
        var invoices = await db.Invoices.AsNoTracking()
            .Include(i => i.PaymentLines)
            .Include(i => i.Session)!.ThenInclude(s => s!.Station)
            .Where(i => i.WorkingDayYear == year && i.WorkingDayMonth == month && i.WorkingDayDay == day)
            .ToListAsync(ct);

        var invoiceIds = invoices.Select(i => i.Id).ToList();
        var packageInvoiceIds = await db.WalletLedger.AsNoTracking()
            .Where(w => w.Kind == WalletLedgerKind.PackagePurchase && w.InvoiceId != null && invoiceIds.Contains(w.InvoiceId.Value))
            .Select(w => w.InvoiceId!.Value)
            .ToHashSetAsync(ct);

        long play = 0;
        long buffet = 0;
        long services = 0;
        long rounding = 0;
        long surcharge = 0;
        long package = 0;
        var received = new MethodTotals(0, 0, 0);
        var byStation = new Dictionary<string, long>();
        var byOperator = new Dictionary<Guid, long>();

        foreach (var invoice in invoices)
        {
            if (invoice.Status == "void")
            {
                continue;
            }

            play = MoneyMath.CheckedAdd(play, invoice.TimeGrossToman);
            buffet = MoneyMath.CheckedAdd(buffet, invoice.BuffetGrossToman);
            services = MoneyMath.CheckedAdd(services, invoice.ServicesGrossToman);
            rounding = MoneyMath.CheckedAdd(rounding, invoice.RoundingToman);
            surcharge = MoneyMath.CheckedAdd(surcharge, invoice.SurchargeToman);
            if (packageInvoiceIds.Contains(invoice.Id))
            {
                package = MoneyMath.CheckedAdd(package, invoice.CollectibleToman);
            }

            var methods = ShiftIncomeMath.SumReceivedMethods(invoice.PaymentLines);
            received = new MethodTotals(
                MoneyMath.CheckedAdd(received.Cash, methods.Cash),
                MoneyMath.CheckedAdd(received.CardReader, methods.CardReader),
                MoneyMath.CheckedAdd(received.CardToCardConfirmed, methods.CardToCardConfirmed));

            var stationKey = invoice.Session?.StationId ?? "(buffet)";
            byStation[stationKey] = byStation.GetValueOrDefault(stationKey) + ShiftIncomeMath.ReceivedMoneyTotal(methods, new MethodTotals(0, 0, 0));
            if (invoice.SettledByUserId is Guid uid)
            {
                byOperator[uid] = byOperator.GetValueOrDefault(uid) + ShiftIncomeMath.ReceivedMoneyTotal(methods, new MethodTotals(0, 0, 0));
            }
        }

        var topUps = await db.WalletLedger.AsNoTracking()
            .Where(w => w.Kind == WalletLedgerKind.TopUpReal
                        && w.WorkingDayYear == year
                        && w.WorkingDayMonth == month
                        && w.WorkingDayDay == day)
            .ToListAsync(ct);

        long walletTopUp = topUps.Sum(w => w.RealDeltaToman);
        var walletMethods = ShiftIncomeMath.SumWalletTopUpReceived(topUps);
        received = new MethodTotals(
            MoneyMath.CheckedAdd(received.Cash, walletMethods.Cash),
            MoneyMath.CheckedAdd(received.CardReader, walletMethods.CardReader),
            MoneyMath.CheckedAdd(received.CardToCardConfirmed, walletMethods.CardToCardConfirmed));

        foreach (var row in topUps)
        {
            if (row.ActorUserId is not Guid actorId)
            {
                continue;
            }

            var rowMethods = ShiftIncomeMath.SumWalletTopUpReceived(new[] { row });
            byOperator[actorId] = byOperator.GetValueOrDefault(actorId)
                + ShiftIncomeMath.ReceivedMoneyTotal(rowMethods, new MethodTotals(0, 0, 0));
        }

        var destinationCards = await BuildDestinationCardsForDayAsync(year, month, day, ct);

        return new
        {
            workingDay = new { year, month, day },
            playToman = play,
            buffetToman = buffet,
            servicesToman = services,
            roundingToman = rounding,
            surchargeToman = surcharge,
            packageSalesToman = package,
            walletTopUpToman = walletTopUp,
            receivedCashToman = received.Cash,
            receivedCardReaderToman = received.CardReader,
            receivedCardToCardToman = received.CardToCardConfirmed,
            receivedMoneyToman = ShiftIncomeMath.ReceivedMoneyTotal(received, new MethodTotals(0, 0, 0)),
            byStation = byStation.Select(kv => new { stationId = kv.Key, receivedMoneyToman = kv.Value }).ToList(),
            byOperator = byOperator.Select(kv => new { operatorId = kv.Key, receivedMoneyToman = kv.Value }).ToList(),
            destinationCards,
        };
    }

    private static object RollUpIncomeRows(IReadOnlyList<object> rows, object period)
    {
        long SumLong(Func<dynamic, long> pick)
        {
            long total = 0;
            foreach (dynamic row in rows)
            {
                total = MoneyMath.CheckedAdd(total, pick(row));
            }

            return total;
        }

        return new
        {
            period,
            playToman = SumLong(r => r.playToman),
            buffetToman = SumLong(r => r.buffetToman),
            servicesToman = SumLong(r => r.servicesToman),
            roundingToman = SumLong(r => r.roundingToman),
            surchargeToman = SumLong(r => r.surchargeToman),
            packageSalesToman = SumLong(r => r.packageSalesToman),
            walletTopUpToman = SumLong(r => r.walletTopUpToman),
            receivedCashToman = SumLong(r => r.receivedCashToman),
            receivedCardReaderToman = SumLong(r => r.receivedCardReaderToman),
            receivedCardToCardToman = SumLong(r => r.receivedCardToCardToman),
            receivedMoneyToman = SumLong(r => r.receivedMoneyToman),
            days = rows,
        };
    }

    private async Task<List<LocalDate>> ListWorkingDaysInJalaliMonthAsync(int jalaliYear, int jalaliMonth, CancellationToken ct)
    {
        var invoiceDays = await db.Invoices.AsNoTracking()
            .Where(i => i.WorkingDayYear != null)
            .Select(i => new { i.WorkingDayYear, i.WorkingDayMonth, i.WorkingDayDay })
            .Distinct()
            .ToListAsync(ct);
        var walletDays = await db.WalletLedger.AsNoTracking()
            .Where(w => w.WorkingDayYear != null)
            .Select(w => new { w.WorkingDayYear, w.WorkingDayMonth, w.WorkingDayDay })
            .Distinct()
            .ToListAsync(ct);

        var days = new HashSet<LocalDate>();
        foreach (var row in invoiceDays)
        {
            if (row.WorkingDayYear is not int y || row.WorkingDayMonth is not int m || row.WorkingDayDay is not int d)
            {
                continue;
            }

            var ld = new LocalDate(y, m, d);
            var jalali = JalaliDate.FromGregorian(ld);
            if (jalali.Year == jalaliYear && jalali.Month == jalaliMonth)
            {
                days.Add(ld);
            }
        }

        foreach (var row in walletDays)
        {
            if (row.WorkingDayYear is not int y || row.WorkingDayMonth is not int m || row.WorkingDayDay is not int d)
            {
                continue;
            }

            var ld = new LocalDate(y, m, d);
            var jalali = JalaliDate.FromGregorian(ld);
            if (jalali.Year == jalaliYear && jalali.Month == jalaliMonth)
            {
                days.Add(ld);
            }
        }

        return days.OrderBy(d => d.Year).ThenBy(d => d.Month).ThenBy(d => d.Day).ToList();
    }

    private async Task<MethodTotals> ComputeExpectedForCloseRecordAsync(CloseRecordEntity close, CancellationToken ct)
    {
        long cash = 0;
        long card = 0;
        long c2c = 0;
        if (close.ShiftId is Guid shiftId)
        {
            var shift = await db.Shifts.AsNoTracking().FirstAsync(s => s.Id == shiftId, ct);
            cash = shift.OpeningFloatToman;
        }

        var invoices = await db.Invoices.AsNoTracking()
            .Include(i => i.PaymentLines)
            .Where(i => i.CloseRecordId == close.Id && i.Status != "void")
            .ToListAsync(ct);
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
                else if (line.Method == PaymentMethod.CardToCard
                         && (line.ConfirmationStatus == PaymentConfirmationStatus.Confirmed
                             || line.ConfirmationStatus == PaymentConfirmationStatus.Settled))
                {
                    c2c = MoneyMath.CheckedAdd(c2c, line.AmountToman);
                }
            }
        }

        IQueryable<WalletLedgerEntryEntity> walletQuery = db.WalletLedger.AsNoTracking()
            .Where(w => w.Kind == WalletLedgerKind.TopUpReal);
        if (close.ShiftId is Guid closeShiftId)
        {
            walletQuery = walletQuery.Where(w => w.ShiftId == closeShiftId);
        }
        else
        {
            walletQuery = walletQuery.Where(w => w.ShiftId == null
                                                 && w.WorkingDayYear == close.WorkingDayYear
                                                 && w.WorkingDayMonth == close.WorkingDayMonth
                                                 && w.WorkingDayDay == close.WorkingDayDay);
        }

        var topUps = await walletQuery.ToListAsync(ct);
        foreach (var row in topUps)
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
                    c2c = MoneyMath.CheckedAdd(c2c, row.RealDeltaToman);
                    break;
            }
        }

        return new MethodTotals(cash, card, c2c);
    }

    private async Task<List<object>> BuildDestinationCardsForDayAsync(int year, int month, int day, CancellationToken ct)
    {
        var invoiceRows = await (
            from p in db.PaymentLines.AsNoTracking()
            join i in db.Invoices.AsNoTracking() on p.InvoiceId equals i.Id
            where p.Method == PaymentMethod.CardToCard
                  && p.DestinationCardId != null
                  && i.Status != "void"
                  && i.WorkingDayYear == year
                  && i.WorkingDayMonth == month
                  && i.WorkingDayDay == day
                  && (p.ConfirmationStatus == PaymentConfirmationStatus.Confirmed
                      || p.ConfirmationStatus == PaymentConfirmationStatus.Settled)
            select new { CardId = p.DestinationCardId!, Amount = p.AmountToman }).ToListAsync(ct);

        var walletRows = await db.WalletLedger.AsNoTracking()
            .Where(w => w.Kind == WalletLedgerKind.TopUpReal
                        && w.Method == PaymentMethod.CardToCard
                        && w.DestinationCardId != null
                        && w.WorkingDayYear == year
                        && w.WorkingDayMonth == month
                        && w.WorkingDayDay == day)
            .Select(w => new { CardId = w.DestinationCardId!, Amount = w.RealDeltaToman })
            .ToListAsync(ct);

        return invoiceRows.Concat(walletRows)
            .GroupBy(x => x.CardId)
            .Select(g => (object)new { destinationCardId = g.Key, totalToman = g.Sum(x => x.Amount) })
            .ToList();
    }

    private void EnsureReportRole()
    {
        if (op.Role == UserRole.Operator)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Operators cannot view aggregate financial reports.");
        }
    }
}
