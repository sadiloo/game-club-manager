namespace GameClub.Kiosk.Core;

public sealed record GuardOptions
{
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public int MaxRestarts { get; init; } = 5;

    public TimeSpan RestartWindow { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan AdminEscapeDuration { get; init; } = TimeSpan.FromMinutes(10);

    public static GuardOptions Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new GuardOptions();
        }

        try
        {
            var file = System.Text.Json.JsonSerializer.Deserialize<GuardOptionsFile>(json!);
            if (file is null)
            {
                return new GuardOptions();
            }

            return new GuardOptions
            {
                HeartbeatTimeout = file.HeartbeatTimeoutSeconds > 0
                    ? TimeSpan.FromSeconds(file.HeartbeatTimeoutSeconds)
                    : TimeSpan.FromSeconds(15),
                MaxRestarts = file.MaxRestarts > 0 ? file.MaxRestarts : 5,
                RestartWindow = file.RestartWindowSeconds > 0
                    ? TimeSpan.FromSeconds(file.RestartWindowSeconds)
                    : TimeSpan.FromMinutes(5),
                AdminEscapeDuration = file.AdminEscapeSeconds > 0
                    ? TimeSpan.FromSeconds(file.AdminEscapeSeconds)
                    : TimeSpan.FromMinutes(10),
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return new GuardOptions();
        }
    }

    private sealed class GuardOptionsFile
    {
        public int HeartbeatTimeoutSeconds { get; set; }

        public int MaxRestarts { get; set; }

        public int RestartWindowSeconds { get; set; }

        public int AdminEscapeSeconds { get; set; }
    }
}

public sealed record GuardSnapshot(
    bool ShellRunning,
    DateTimeOffset? LastHeartbeat,
    DateTimeOffset? AdminEscapeUntil,
    IReadOnlyList<DateTimeOffset> RecentRestarts,
    DateTimeOffset? ShellObservedAt = null,
    bool Maintenance = false);

public sealed record GuardDecision(
    bool RestartShell,
    bool EnforceBlocklist,
    string Reason,
    bool LogOffKiosk = false,
    bool EnterMaintenance = false);

public static class GuardAdvisor
{
    public static GuardDecision Decide(GuardSnapshot snapshot, DateTimeOffset now, GuardOptions? options = null)
    {
        options ??= new GuardOptions();
        if (snapshot.Maintenance)
        {
            return new GuardDecision(false, false, "maintenance");
        }

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

        var startupGrace = false;
        var hung = false;
        if (snapshot.ShellRunning && snapshot.LastHeartbeat is not DateTimeOffset beat)
        {
            var observed = snapshot.ShellObservedAt ?? now;
            if (now - observed <= options.HeartbeatTimeout)
            {
                startupGrace = true;
            }
            else
            {
                hung = true;
            }
        }
        else if (snapshot.ShellRunning && snapshot.LastHeartbeat is DateTimeOffset heard && now - heard > options.HeartbeatTimeout)
        {
            hung = true;
        }

        var dead = !snapshot.ShellRunning;
        if (startupGrace)
        {
            return new GuardDecision(false, true, "startup_grace");
        }

        if (!dead && !hung)
        {
            return new GuardDecision(false, true, "healthy");
        }

        if (restarts >= options.MaxRestarts)
        {
            return new GuardDecision(false, false, "restart_backoff", LogOffKiosk: true, EnterMaintenance: true);
        }

        return new GuardDecision(true, true, dead ? "shell_not_running" : "heartbeat_timeout");
    }

    public static IReadOnlyList<DateTimeOffset> Prune(IReadOnlyList<DateTimeOffset>? restarts, DateTimeOffset now, TimeSpan window)
    {
        var kept = new List<DateTimeOffset>();
        foreach (var restart in restarts ?? [])
        {
            if (now - restart < window)
            {
                kept.Add(restart);
            }
        }

        return kept;
    }
}
