namespace GameClub.Kiosk.Core.Protocol;

public sealed class SessionLeasePayload
{
    public Guid LeaseId { get; init; }
    public Guid SessionId { get; init; }
    public string StationId { get; init; } = "";
    public Guid? CustomerId { get; init; }
    public string PrepaidKind { get; init; } = "None";
    public DateTime AuthorizedUntilUtc { get; init; }
    public DateTime MaxBillableCheckpointUtc { get; init; }
    public string WalletDigest { get; init; } = "";
    public long PackageMinutesTotal { get; init; }
    public long CatalogVersion { get; init; }
    public DateTime IssuedAtUtc { get; init; }
    public int LeaseVersion { get; init; }
}

public sealed class SignedSessionLease
{
    public SessionLeasePayload Payload { get; init; } = new();
    public string SignatureBase64 { get; init; } = "";
}

public sealed class KioskHeartbeatRequest
{
    public Guid? SessionId { get; init; }
    public long ClientMonotonicMs { get; init; }
}

public sealed class KioskHeartbeatAck
{
    public DateTime ServerUtc { get; init; }
    public DateTime? LastAcceptedCheckpointUtc { get; init; }
    public string SessionStatus { get; init; } = "";
    public SignedSessionLease? Lease { get; init; }
    public KioskDisplayDto? Display { get; init; }
    public bool ManualSettle { get; init; }
    public DateTime? DeadPcDeadlineUtc { get; init; }
}

public sealed class KioskDisplayDto
{
    public long? WalletBalanceToman { get; init; }
    public long? GiftBalanceToman { get; init; }
    public IReadOnlyList<int>? PackageMinutesRemaining { get; init; }
    public long? EstimatedRemainingSeconds { get; init; }
    public long? ElapsedDisplaySeconds { get; init; }
    public string? FreeBandText { get; init; }
    public bool GraceActive { get; init; }
    public bool BlockedByDebt { get; init; }
    public IReadOnlyList<int> NearEndMinutesThresholds { get; init; } = Array.Empty<int>();
    public int? WarningRemainingMinutes { get; init; }
    public bool OpenGuestElapsedOnly { get; init; }
}

public sealed class KioskOutboundEvent
{
    public Guid EventId { get; init; }
    public string Type { get; init; } = "";
    public string PayloadJson { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
}
