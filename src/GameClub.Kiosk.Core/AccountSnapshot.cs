using System.Text.Json;
using GameClub.Billing;

namespace GameClub.Kiosk.Core;

public sealed class AccountSnapshot
{
    public string Id { get; set; } = "";

    public int WalletMode { get; set; }

    public bool AllowPcWhileInDebt { get; set; }

    public bool UnlimitedDebt { get; set; } = true;

    public long MaxDebtToman { get; set; }

    public bool GraceMinuteEnabled { get; set; } = true;

    public long PcReal { get; set; }

    public long PcGift { get; set; }

    public long NonPcReal { get; set; }

    public long NonPcGift { get; set; }

    public List<PackageSnapshot> Packages { get; set; } = [];
}

public sealed class PackageSnapshot
{
    public string Id { get; set; } = "";

    public long RemainingMinutes { get; set; }

    public long? FaceValueToman { get; set; }

    public List<string> AllowedGroupIds { get; set; } = [];

    public string? AllowedFrom { get; set; }

    public string? AllowedUntil { get; set; }

    public string? ValidThrough { get; set; }
}

public sealed class OpenSessionSnapshot
{
    public string SessionId { get; set; } = "";

    public string MemberName { get; set; } = "";

    public int? MemberAge { get; set; }

    public long LoginRevision { get; set; }

    public string StartedAt { get; set; } = "";

    public string? ActiveGameId { get; set; }

    public AccountSnapshot? AccountAtLogin { get; set; }

    public string StationId { get; set; } = "";

    public string GroupId { get; set; } = "";

    public int Mode { get; set; }

    public int PlayerCount { get; set; } = 1;

    public long HourlyToman { get; set; }
}

public static class AccountCodec
{
    public static AccountSnapshot From(CustomerAccount account)
    {
        var packages = new List<PackageSnapshot>();
        foreach (var package in account.Packages)
        {
            packages.Add(new PackageSnapshot
            {
                Id = package.Id,
                RemainingMinutes = package.RemainingMinutes,
                FaceValueToman = package.FaceValueToman,
                AllowedGroupIds = package.AllowedGroupIds is null ? [] : package.AllowedGroupIds.ToList(),
                AllowedFrom = package.AllowedFrom is TimeOnly from ? Clock(from) : null,
                AllowedUntil = package.AllowedUntil is TimeOnly until ? Clock(until) : null,
                ValidThrough = package.ValidThrough is DateOnly date ? $"{date.Year:0000}-{date.Month:00}-{date.Day:00}" : null,
            });
        }

        return new AccountSnapshot
        {
            Id = account.Id,
            WalletMode = (int)account.Settings.Mode,
            AllowPcWhileInDebt = account.Settings.AllowPcWhileInDebt,
            UnlimitedDebt = account.Settings.Debt.MaxDebtToman is null,
            MaxDebtToman = account.Settings.Debt.MaxDebtToman ?? 0,
            GraceMinuteEnabled = account.Settings.GraceMinuteEnabled,
            PcReal = account.Pc.RealToman,
            PcGift = account.Pc.GiftToman,
            NonPcReal = account.NonPc.RealToman,
            NonPcGift = account.NonPc.GiftToman,
            Packages = packages,
        };
    }

    public static CustomerAccount ToAccount(AccountSnapshot snapshot)
    {
        var debt = snapshot.UnlimitedDebt
            ? DebtPolicy.Unlimited
            : snapshot.MaxDebtToman == 0
                ? DebtPolicy.None
                : DebtPolicy.Limited(snapshot.MaxDebtToman);
        var settings = new WalletSettings((WalletMode)snapshot.WalletMode, snapshot.AllowPcWhileInDebt, debt, snapshot.GraceMinuteEnabled);
        var packages = new List<TimePackage>();
        foreach (var package in snapshot.Packages ?? [])
        {
            ISet<string>? groups = package.AllowedGroupIds is { Count: > 0 }
                ? new HashSet<string>(package.AllowedGroupIds, StringComparer.Ordinal)
                : null;
            TimeOnly? from = package.AllowedFrom is null ? null : ParseClock(package.AllowedFrom);
            TimeOnly? until = package.AllowedUntil is null ? null : ParseClock(package.AllowedUntil);
            DateOnly? through = package.ValidThrough is null ? null : ParseDate(package.ValidThrough);
            packages.Add(new TimePackage(package.Id, package.RemainingMinutes, package.FaceValueToman, groups, from, until, through));
        }

        return new CustomerAccount(
            snapshot.Id,
            settings,
            new WalletPocket(snapshot.PcReal, snapshot.PcGift),
            new WalletPocket(snapshot.NonPcReal, snapshot.NonPcGift),
            packages);
    }

    public static string Write(AccountSnapshot snapshot) => JsonSerializer.Serialize(snapshot);

    public static AccountSnapshot Read(string json) =>
        JsonSerializer.Deserialize<AccountSnapshot>(json) ?? throw new InvalidOperationException("Account snapshot is empty.");

    public static string WriteSession(OpenSessionSnapshot snapshot) => JsonSerializer.Serialize(snapshot);

    public static OpenSessionSnapshot ReadSession(string json) =>
        JsonSerializer.Deserialize<OpenSessionSnapshot>(json) ?? throw new InvalidOperationException("Session snapshot is empty.");

    private static string Clock(TimeOnly time) => $"{time.Hour:00}:{time.Minute:00}:{time.Second:00}";

    private static TimeOnly ParseClock(string text)
    {
        var parts = text.Split(':');
        var hour = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var minute = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var second = parts.Length > 2 ? int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
        return new TimeOnly(hour, minute, second);
    }

    private static DateOnly ParseDate(string text)
    {
        var parts = text.Split('-');
        return new DateOnly(
            int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
    }
}
