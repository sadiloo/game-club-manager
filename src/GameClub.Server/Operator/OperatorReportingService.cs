using System.Text;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public sealed class OperatorReportingService(AppDbContext db, OperatorContext op)
{
    public async Task<object> DailyIncomeAsync(int year, int month, int day, CancellationToken ct)
    {
        EnsureReportRole();
        var invoices = await db.Invoices.AsNoTracking()
            .Include(i => i.PaymentLines)
            .Include(i => i.Session)!.ThenInclude(s => s!.Station)
            .Where(i => i.WorkingDayYear == year && i.WorkingDayMonth == month && i.WorkingDayDay == day)
            .ToListAsync(ct);

        long play = 0;
        long buffet = 0;
        long services = 0;
        long rounding = 0;
        long surcharge = 0;
        long package = 0;
        var received = new MethodTotals(0, 0, 0);
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
            if (invoice.SessionId is null && invoice.TimeGrossToman == 0 && invoice.BuffetGrossToman == 0)
            {
                package = MoneyMath.CheckedAdd(package, invoice.CollectibleToman);
            }

            var methods = ShiftIncomeMath.SumReceivedMethods(invoice.PaymentLines);
            received = new MethodTotals(
                MoneyMath.CheckedAdd(received.Cash, methods.Cash),
                MoneyMath.CheckedAdd(received.CardReader, methods.CardReader),
                MoneyMath.CheckedAdd(received.CardToCardConfirmed, methods.CardToCardConfirmed));
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
        };
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
        var q = db.Invoices.AsNoTracking()
            .Include(i => i.PaymentLines)
            .Include(i => i.Session)!.ThenInclude(s => s!.Station)
            .AsQueryable();
        if (fromUtc is DateTime from)
        {
            q = q.Where(i => i.IssuedAtUtc >= from);
        }

        if (toUtc is DateTime to)
        {
            q = q.Where(i => i.IssuedAtUtc <= to);
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
        var history = await HistoryAsync(fromUtc, toUtc, stationId, customerId, operatorId, method, ct);
        var rows = (IEnumerable<object>)history;
        var sb = new StringBuilder();
        sb.AppendLine("number,status,collectible,stationId,settledBy");
        foreach (dynamic row in rows)
        {
            sb.AppendLine($"{row.Number},{row.Status},{row.CollectibleToman},{row.stationId},{row.SettledByUserId}");
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

        var byStation = new Dictionary<string, long>();
        var byHour = new Dictionary<int, long>();
        foreach (var session in sessions)
        {
            foreach (var seg in session.Segments)
            {
                var end = seg.EndUtc ?? toUtc;
                if (end < fromUtc || seg.StartUtc > toUtc)
                {
                    continue;
                }

                var activeSeconds = (long)Math.Max(0, (end - seg.StartUtc).TotalSeconds);
                foreach (var pause in session.Pauses)
                {
                    var pEnd = pause.EndUtc ?? end;
                    if (pEnd > seg.StartUtc && pause.StartUtc < end)
                    {
                        var overlapStart = pause.StartUtc < seg.StartUtc ? seg.StartUtc : pause.StartUtc;
                        var overlapEnd = pEnd > end ? end : pEnd;
                        activeSeconds -= (long)Math.Max(0, (overlapEnd - overlapStart).TotalSeconds);
                    }
                }

                byStation[seg.StationId] = byStation.GetValueOrDefault(seg.StationId) + activeSeconds;
                var tehranHour = TimeMapping.ToTehran(seg.StartUtc).Hour;
                byHour[tehranHour] = byHour.GetValueOrDefault(tehranHour) + activeSeconds;
            }
        }

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
        var close = await db.CloseRecords.AsNoTracking().FirstOrDefaultAsync(c => c.Id == closeRecordId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Close record not found.");
        return await DailyIncomeAsync(close.WorkingDayYear, close.WorkingDayMonth, close.WorkingDayDay, ct);
    }

    private void EnsureReportRole()
    {
        if (op.Role == UserRole.Operator)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Operators cannot view aggregate financial reports.");
        }
    }
}
