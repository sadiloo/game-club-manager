using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Time;

internal static class TestClockBootstrap
{
    private static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("GAMECLUB_ALLOW_TEST_CLOCK"), "1", StringComparison.Ordinal)
        || string.Equals(Environment.GetEnvironmentVariable("GAMECLUB_ALLOW_TEST_CLOCK"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool TryRegister(IServiceCollection services)
    {
        if (!IsEnabled)
        {
            return false;
        }

        var initial = DateTimeOffset.UtcNow;
        var envFixed = Environment.GetEnvironmentVariable("GAMECLUB_TEST_CLOCK_UTC");
        if (!string.IsNullOrWhiteSpace(envFixed)
            && DateTimeOffset.TryParse(envFixed, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            initial = parsed;
        }

        var clock = new MutableClock(initial);
        services.AddSingleton(clock);
        services.AddSingleton<IClock>(clock);
        return true;
    }
}