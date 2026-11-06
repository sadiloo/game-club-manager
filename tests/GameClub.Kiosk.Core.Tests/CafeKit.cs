using GameClub.Billing;

namespace GameClub.Kiosk.Core.Tests;

internal static class CafeKit
{
    public const long Hourly = 60_000;
    public static readonly StationContext Station = new("pc-01", "pc-normal", PlayMode.Offline, 1);

    public static DateTimeOffset At(int hour, int minute, int second = 0) =>
        TehranTime.At(2026, 6, 2, hour, minute, second);

    public static RateCard Rates(long hourly = Hourly) =>
        new RateCardBuilder()
            .AddBase(Station.GroupId, PlayMode.Offline, 1, hourly)
            .Build();

    public static CustomerAccount Money(long toman, bool grace = true, int? ageUnused = null) =>
        new(
            "member-1",
            new WalletSettings(WalletMode.Separate, false, DebtPolicy.Unlimited, grace),
            new WalletPocket(toman, 0),
            new WalletPocket(0, 0),
            []);

    public static CustomerAccount WithPackages(long toman, params TimePackage[] packages) =>
        Money(toman) with { Packages = packages };

    public static InMemoryCafeServer Server(long balance = 200_000, int? age = 20, string user = "reza", string password = "reza-demo")
    {
        var server = new InMemoryCafeServer(Station, Rates(), Hourly, DemoGames());
        server.AddMember(user, password, "رضا", age, Money(balance));
        return server;
    }

    public static List<GameEntry> DemoGames() => DemoStand.Games().ToList();

    public static string TempDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gameclub-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "kiosk.db");
    }
}
