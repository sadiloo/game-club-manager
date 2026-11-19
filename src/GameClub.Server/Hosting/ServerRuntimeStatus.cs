namespace GameClub.Server.Hosting;

/// <summary>In-memory server start time (B1); no persisted state.</summary>
public sealed class ServerRuntimeStatus
{
    public DateTimeOffset? ApplicationStartedAtUtc { get; set; }
}