using GameClub.Billing;

namespace GameClub.Kiosk.Core;

/// <summary>
/// Standalone cafe used when no server is configured. Rates and passwords are demo data,
/// not a live tariff. The admin list is the only set of games the shell will launch.
/// </summary>
public static class DemoStand
{
    public const string StationId = "pc-01";

    public static string GroupId => PilotSeed.PcNormalGroupId;
    public const string AdultUser = "reza";
    public const string AdultPassword = "reza-demo";
    public const string MinorUser = "neda";
    public const string MinorPassword = "neda-demo";

    public static StationContext Station { get; } = new(StationId, GroupId, PlayMode.Offline, 1);

    public static RateCard Rates() => PilotSeed.CreateCard();

    public static IReadOnlyList<GameEntry> Games() =>
    [
        new GameEntry
        {
            Id = "cs2",
            Name = "کانتر استرایک ۲",
            Category = "تیراندازی",
            CoverPath = "covers/cs2.png",
            Launch = "steam://rungameid/730",
            Enabled = true,
        },
        new GameEntry
        {
            Id = "local",
            Name = "بازی محلی",
            Category = "آفلاین",
            Launch = @"C:\Games\Local\local.exe",
            Arguments = "-windowed",
            WorkingDirectory = @"C:\Games\Local",
            Enabled = true,
            HelperProcesses = ["EasyAntiCheat"],
        },
        new GameEntry
        {
            Id = "valorant",
            Name = "والورانت",
            Category = "تیراندازی",
            Launch = @"C:\Riot Games\VALORANT\live\VALORANT.exe",
            MinimumAge = 16,
            Enabled = true,
            HelperProcesses = ["vgc", "vgtray"],
        },
        new GameEntry
        {
            Id = "vip-only",
            Name = "ویژه",
            Category = "ویژه",
            Launch = @"C:\Games\Vip\vip.exe",
            RestrictedGroupId = "vip",
            Enabled = true,
        },
        new GameEntry
        {
            Id = "hidden",
            Name = "خاموش",
            Category = "آفلاین",
            Launch = @"C:\Games\Off\off.exe",
            Enabled = false,
        },
    ];

    public static InMemoryCafeServer CreateServer()
    {
        var server = new InMemoryCafeServer(Station, Rates(), 0, Games());
        server.AddMember(AdultUser, AdultPassword, "رضا", 20, Account("member-reza", 200_000));
        server.AddMember(MinorUser, MinorPassword, "ندا", 15, Account("member-neda", 2_000));
        return server;
    }

    private static CustomerAccount Account(string id, long toman) =>
        new(id, WalletSettings.Separate, new WalletPocket(toman, 0), new WalletPocket(0, 0), []);
}
