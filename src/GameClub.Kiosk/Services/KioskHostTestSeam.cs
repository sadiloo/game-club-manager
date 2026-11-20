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

    public int LoginPostCount { get; private set; }

    public IReadOnlyList<string> LoginPostBodies => _loginPostBodies;

    private readonly List<string> _loginPostBodies = [];

    public void ResetLoginAttemptTrace()
    {
        // Called at the start of each login attempt after precheck passes.
    }

    public void RecordLoginPost() => LoginPostCount++;

    public void RecordLoginPostBody(string body)
    {
        LoginPostCount++;
        if (!string.IsNullOrEmpty(body))
        {
            _loginPostBodies.Add(body);
        }
    }

    public void ResetLoginMetrics()
    {
        LoginPostCount = 0;
        _loginPostBodies.Clear();
    }
}