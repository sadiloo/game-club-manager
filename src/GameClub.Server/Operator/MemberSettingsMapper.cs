using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data.Entities;

namespace GameClub.Server.Operator;

public static class MemberSettingsMapper
{
    public static LevelLadder LoadLadder(BranchMemberSettingsEntity row)
    {
        if (string.IsNullOrWhiteSpace(row.LevelLadderJson) || row.LevelLadderJson == "[]")
        {
            return LevelLadder.Default;
        }

        var levels = JsonSerializer.Deserialize<CustomerLevelDto[]>(row.LevelLadderJson) ?? [];
        if (levels.Length == 0)
        {
            return LevelLadder.Default;
        }

        return LevelLadder.Create(levels.Select(l => new CustomerLevel(l.Name, l.MinimumLifetimeHours, l.DiscountPercent)).ToArray());
    }

    public static string SerializeLadder(LevelLadder ladder)
        => JsonSerializer.Serialize(ladder.Levels.Select(l => new CustomerLevelDto(l.Name, l.MinimumLifetimeHours, l.DiscountPercent)).ToArray());

    public static DiscountPolicy LoadDiscountPolicy(BranchMemberSettingsEntity row)
    {
        var birthday = row.BirthdayWindowKind switch
        {
            "birth_month" => BirthdayWindow.WholeMonth,
            "day_window" when row.BirthdayWindowAnchor == "around" => BirthdayWindow.DaysAround(row.BirthdayWindowDays),
            "day_window" => BirthdayWindow.DaysOnAndAfter(row.BirthdayWindowDays),
            _ => BirthdayWindow.ExactDay,
        };
        return new DiscountPolicy(row.MaxCombinedDiscountPercent, birthday);
    }

    public static AccountPolicy LoadAccountPolicy(BranchMemberSettingsEntity row)
        => row.PackageConsumptionOrder == "money_then_packages"
            ? new AccountPolicy(ConsumptionOrder.MoneyThenPackages)
            : AccountPolicy.Default;

    public static DebtPolicy LoadDebtPolicy(BranchMemberSettingsEntity row)
        => row.WalletDebtFloorToman is long floor
            ? floor == 0 ? DebtPolicy.None : DebtPolicy.Limited(-floor)
            : DebtPolicy.None;

    public sealed record CustomerLevelDto(string Name, long MinimumLifetimeHours, int DiscountPercent);

    public static JalaliDate? LoadBirthDate(CustomerMemberEntity? member)
    {
        if (member?.BirthYear is not int y || member.BirthMonth is not int m || member.BirthDay is not int d)
        {
            return null;
        }

        return new JalaliDate(y, m, d);
    }

    public static CustomerPricing? BuildPricing(CustomerMemberEntity? member, BranchMemberSettingsEntity settings, LevelLadder ladder)
    {
        if (member is null)
        {
            return null;
        }

        var hours = member.LifetimePlayMinutes / 60;
        var level = ladder.Resolve(hours);
        var birth = LoadBirthDate(member);
        var levelPercent = level?.DiscountPercent ?? 0;
        var levelName = level?.Name;
        var birthdayPercent = birth is { IsEmpty: false } ? settings.BirthdayPercent : 0;
        return new CustomerPricing(levelName, levelPercent, birth, birthdayPercent);
    }
}
