using GameClub.Billing;

namespace GameClub.Billing.Tests;

internal static class Ss14Fc27Rates
{
    public const int GameTitleId = 9001;
    public const string DisplayName = "FC27";

    public static PlaySegment WithGame(PlaySegment seg)
        => seg with
        {
            GameTitleId = GameTitleId,
            GameDisplayName = DisplayName,
            GameHourlyRateToman = Hourly(seg.PlayerCount, seg.Mode),
            GameNightHourlyRateToman = Night(seg.PlayerCount, seg.Mode),
        };

    public static PlaySegment WithGameNoNight(PlaySegment seg, int players = 1)
        => seg with
        {
            GameTitleId = GameTitleId,
            GameDisplayName = DisplayName,
            GameHourlyRateToman = Hourly(players, PlayMode.Offline),
            GameNightHourlyRateToman = null,
        };

    public static long Hourly(int players, PlayMode mode)
        => mode == PlayMode.Online
            ? 190_000
            : players switch
            {
                1 => 150_000,
                2 => 170_000,
                3 => 200_000,
                4 => 260_000,
                _ => throw new ArgumentOutOfRangeException(nameof(players)),
            };

    public static long? Night(int players, PlayMode mode)
        => mode == PlayMode.Online
            ? 220_000
            : players switch
            {
                1 => 180_000,
                2 => 200_000,
                3 => 230_000,
                4 => 290_000,
                _ => throw new ArgumentOutOfRangeException(nameof(players)),
            };
}