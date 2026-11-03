namespace GameClub.Kiosk.Core;

public sealed record GuardOptions
{
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public int MaxRestarts { get; init; } = 5;

    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan AdminEscapeDuration { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record GuardSnapshot(
    bool ShellRunning,
    DateTimeOffset? LastHeartbeat,
    DateTimeOffset? AdminEscapeUntil,
    IReadOnlyList<DateTimeOffset> RecentRestarts);

public sealed record GuardDecision(bool RestartShell, bool EnforceBlocklist, string Reason);

public static class GuardAdvisor
{
    public static GuardDecision Decide(GuardSnapshot snapshot, DateTimeOffset now, GuardOptions? options = null)
    {
        options ??= new GuardOptions();
        if (snapshot.AdminEscapeUntil is DateTimeOffset until && now < until)
        {
            return new GuardDecision(false, false, "admin_escape");
        }

        var restarts = 0;
        foreach (var restart in snapshot.RecentRestarts ?? [])
        {
            if (now - restart < options.RestartWindow)
            {
                restarts++;
            }
        }

        var hung = snapshot.ShellRunning
            && snapshot.LastHeartbeat is DateTimeOffset beat
            && now - beat > options.HeartbeatTimeout;
        var dead = !snapshot.ShellRunning;
        if (!dead && !hung)
        {
            return new GuardDecision(false, true, "healthy");
        }

        if (restarts >= options.MaxRestarts)
        {
            return new GuardDecision(false, true, "restart_backoff");
        }

        return new GuardDecision(true, true, dead ? "shell_not_running" : "heartbeat_timeout");
    }
}
