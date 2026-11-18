namespace GameClub.Kiosk.Services;

/// <summary>Optional test-only flags for WPF host integration tests; production passes null.</summary>
public sealed class KioskHostTestSeam
{
    public bool SessionCleanupThrows { get; init; }

    public bool SkipLogoutHttp { get; init; }

    public TimeSpan? LogoutHttpDelay { get; init; }

    public bool SimulateSuccessfulLogoutHttp { get; init; }

    public bool LogoutHttpThrows { get; init; }
}