using GameClub.Billing;

namespace GameClub.Server.Data.Entities;

public enum SessionStatus
{
    Open = 0,
    Paused = 1,
    Closed = 2,
    ManualSettle = 3,
}

public sealed class StationGroupEntity
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public StationKind Kind { get; set; }
}

public sealed class StationEntity
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string GroupId { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public StationGroupEntity? Group { get; set; }
}

public sealed class PlaySessionEntity
{
    public Guid Id { get; set; }

    public string StationId { get; set; } = string.Empty;

    public StationEntity? Station { get; set; }

    public SessionStatus Status { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? EndedAtUtc { get; set; }

    public PlayMode Mode { get; set; }

    public int PlayerCount { get; set; }

    public DateTime? LastCheckpointUtc { get; set; }

    public string? ManualSettleReason { get; set; }

    public List<PlaySegmentEntity> Segments { get; set; } = [];

    public List<PauseIntervalEntity> Pauses { get; set; } = [];

    public List<CheckpointEntity> Checkpoints { get; set; } = [];

    public InvoiceEntity? Invoice { get; set; }
}

public sealed class PlaySegmentEntity
{
    public long Id { get; set; }

    public Guid SessionId { get; set; }

    public PlaySessionEntity? Session { get; set; }

    public string SegmentKey { get; set; } = string.Empty;

    public DateTime StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    public string StationId { get; set; } = string.Empty;

    public string GroupId { get; set; } = string.Empty;

    public PlayMode Mode { get; set; }

    public int PlayerCount { get; set; }

    public StationKind Kind { get; set; }
}

public sealed class PauseIntervalEntity
{
    public long Id { get; set; }

    public Guid SessionId { get; set; }

    public PlaySessionEntity? Session { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }
}

public sealed class CheckpointEntity
{
    public long Id { get; set; }

    public Guid SessionId { get; set; }

    public PlaySessionEntity? Session { get; set; }

    public DateTime AtUtc { get; set; }
}

public sealed class InvoiceEntity
{
    public Guid Id { get; set; }

    public long Number { get; set; }

    public Guid SessionId { get; set; }

    public PlaySessionEntity? Session { get; set; }

    public long NetBeforeRoundingToman { get; set; }

    public long RoundingToman { get; set; }

    public long PayableToman { get; set; }

    public DateTime IssuedAtUtc { get; set; }

    public string Status { get; set; } = "issued";
}

public sealed class InvoiceNumberSequence
{
    public int Id { get; set; }

    public long NextNumber { get; set; } = 1;
}

public sealed class AuditLogEntry
{
    public long Id { get; set; }

    public DateTime AtUtc { get; set; }

    public string Action { get; set; } = string.Empty;

    public string DetailsJson { get; set; } = "{}";
}

public sealed class RateCardSnapshotEntity
{
    public int Id { get; set; }

    public string Json { get; set; } = string.Empty;
}

public sealed class BranchRoundingEntity
{
    public int Id { get; set; }

    public long StepToman { get; set; }

    public string Direction { get; set; } = "Up";
}

public sealed class RateLineEntity
{
    public long Id { get; set; }

    public string GroupId { get; set; } = string.Empty;

    public string PlayMode { get; set; } = string.Empty;

    public int? PlayerCount { get; set; }

    /// <summary>False = base hourly; true = 00:00–08:30 special band rate.</summary>
    public bool IsSpecialBand { get; set; }

    public long HourlyToman { get; set; }
}
