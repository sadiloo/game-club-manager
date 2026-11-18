namespace GameClub.Kiosk.Services;

/// <summary>Optional test-only flags for WPF host integration tests; production passes null.</summary>
public sealed class KioskHostTestSeam
{
    public bool SessionCleanupThrows { get; init; }

    public bool SkipLogoutHttp { get; init; }

    public TimeSpan? LogoutHttpDelay { get; init; }

    public bool SimulateSuccessfulLogoutHttp { get; init; }

    public bool LogoutHttpThrows { get; init; }

    /// <summary>When set, skips real HTTP and treats this status as the logout POST result.</summary>
    public int? LogoutHttpSimulatedStatusCode { get; init; }
}