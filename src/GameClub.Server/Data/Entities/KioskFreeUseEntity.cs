namespace GameClub.Server.Data.Entities;

public sealed class KioskFreeUseEntity
{
    public Guid Id { get; set; }

    public string StationId { get; set; } = "";

    public DateTime StartedAtUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    public string? EndReason { get; set; }

    public Guid KioskDeviceId { get; set; }

    public string ActorRole { get; set; } = "operator";
}