using GameClub.Billing;

namespace GameClub.Server.Data.Entities;

public enum SessionStatus
{
    Open = 0,
    Paused = 1,
    Closed = 2,
    ManualSettle = 3,
    BillOpen = 4,
    AwaitingPayment = 5,
}

public enum UserRole
{
    Owner = 0,
    Manager = 1,
    Operator = 2,
}

public enum PaymentConfirmationStatus
{
    Settled = 0,
    Pending = 1,
    Confirmed = 2,
    Rejected = 3,
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

    public int DefaultPlayerCount { get; set; } = 1;

    public PlayMode DefaultPlayMode { get; set; } = PlayMode.Offline;

    public StationGroupEntity? Group { get; set; }
}

public sealed class CustomerEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Mobile { get; set; } = string.Empty;

    public string? MembershipCode { get; set; }

    public string? MembershipCodeKey { get; set; }

    public DateTime CreatedAtUtc { get; set; }
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

    public int RateCardSnapshotId { get; set; }

    public RateCardSnapshotEntity? RateCardSnapshot { get; set; }

    public Guid? CustomerId { get; set; }

    public CustomerEntity? Customer { get; set; }

    public PrepaidSessionKind PrepaidKind { get; set; }

    /// <summary>Booked active seconds for prepaid duration/amount play (pause excluded).</summary>
    public long? PrepaidBookedActiveSeconds { get; set; }

    public long? PrepaidAmountBudgetToman { get; set; }

    public bool PrepaidEndAlertSent { get; set; }

    public List<PlaySegmentEntity> Segments { get; set; } = [];

    public List<PauseIntervalEntity> Pauses { get; set; } = [];

    public List<CheckpointEntity> Checkpoints { get; set; } = [];

    public InvoiceEntity? Invoice { get; set; }

    public PendingBillEntity? PendingBill { get; set; }
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

    public Guid? SessionId { get; set; }

    public PlaySessionEntity? Session { get; set; }

    public Guid? OpenSaleId { get; set; }

    public OpenSaleEntity? OpenSale { get; set; }

    public long TimeGrossToman { get; set; }

    public long BuffetGrossToman { get; set; }

    public long ServicesGrossToman { get; set; }

    /// <summary>Exact session payable before PA-8 rounding (unrounded).</summary>
    public long UnroundedPayableToman { get; set; }

    public long RoundingToman { get; set; }

    /// <summary>Rounded amount to collect (step 5,000 up).</summary>
    public long CollectibleToman { get; set; }

    public long SurchargeToman { get; set; }

    public DateTime BilledToUtc { get; set; }

    public DateTime IssuedAtUtc { get; set; }

    /// <summary>confirmed | void</summary>
    public string Status { get; set; } = "confirmed";

    public string? VoidReason { get; set; }

    public string? StopReason { get; set; }

    public Guid? CustomerId { get; set; }

    public CustomerEntity? Customer { get; set; }

    public List<PaymentLineEntity> PaymentLines { get; set; } = [];

    public List<InvoiceCatalogLineEntity> CatalogLines { get; set; } = [];

    public InvoiceMemberEffectEntity? MemberEffect { get; set; }

    public long DiscountToman { get; set; }
}

public sealed class PendingBillEntity
{
    public Guid Id { get; set; }

    public Guid? SessionId { get; set; }

    public PlaySessionEntity? Session { get; set; }

    public Guid? OpenSaleId { get; set; }

    public OpenSaleEntity? OpenSale { get; set; }

    public long TimeGrossToman { get; set; }

    public long BuffetGrossToman { get; set; }

    public long ServicesGrossToman { get; set; }

    public DateTime FrozenAtUtc { get; set; }

    public DateTime OpenedAtUtc { get; set; }

    public long UnroundedPayableToman { get; set; }

    public long RoundingToman { get; set; }

    public long CollectibleToman { get; set; }

    public long SurchargeToman { get; set; }

    /// <summary>console_bill | pc_stop | manual_settle | auto_close</summary>
    public string Source { get; set; } = string.Empty;

    public string? StopReason { get; set; }

    public string? RevertStateJson { get; set; }

    public uint RowVersion { get; set; }
}

public sealed class PaymentLineEntity
{
    public long Id { get; set; }

    public Guid InvoiceId { get; set; }

    public InvoiceEntity? Invoice { get; set; }

    public PaymentMethod Method { get; set; }

    public long AmountToman { get; set; }

    public string? DestinationCardId { get; set; }

    public DestinationCardEntity? DestinationCard { get; set; }

    public string? Reference { get; set; }

    public PaymentConfirmationStatus ConfirmationStatus { get; set; } = PaymentConfirmationStatus.Settled;

    public Guid? WalletCustomerId { get; set; }

    public long WalletRealDrawnToman { get; set; }

    public long WalletGiftDrawnToman { get; set; }
}

public sealed class DestinationCardEntity
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

public sealed class OperatorUserEntity
{
    public Guid Id { get; set; }

    public string Username { get; set; } = string.Empty;

    public string PinHash { get; set; } = string.Empty;

    public UserRole Role { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class RejectedCardToCardEntity
{
    public long Id { get; set; }

    public long PaymentLineId { get; set; }

    public PaymentLineEntity? PaymentLine { get; set; }

    public long AmountToman { get; set; }

    public DateTime RecordedAtUtc { get; set; }
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

    public Guid? ActorUserId { get; set; }
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

    /// <summary>Added to PC base hourly rate inside 00:00–08:30 (pilot: 20,000).</summary>
    public long PcSpecialBandSurchargeToman { get; set; } = 20_000;
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

public enum CatalogProductKind
{
    Stocked = 0,
    Unstocked = 1,
    ServiceFixed = 2,
    ServiceCustom = 3,
}

public enum SaleLineKind
{
    Buffet = 0,
    Service = 1,
}

public static class BuffetSaleConstants
{
    public const string VirtualStationId = "buffet-only";

    public const string VirtualGroupId = "buffet-only";
}

public sealed class CatalogCategoryEntity
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public List<CatalogItemEntity> Items { get; set; } = [];
}

public sealed class CatalogItemEntity
{
    public string Id { get; set; } = string.Empty;

    public string CategoryId { get; set; } = string.Empty;

    public CatalogCategoryEntity? Category { get; set; }

    public string Name { get; set; } = string.Empty;

    public CatalogProductKind Kind { get; set; }

    public long UnitPriceToman { get; set; }

    /// <summary>Stocks for <see cref="CatalogProductKind.Stocked"/> only.</summary>
    public int? StockQuantity { get; set; }

    public int? LowStockWarningThreshold { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class OpenSaleEntity
{
    public Guid Id { get; set; }

    public Guid? SessionId { get; set; }

    public PlaySessionEntity? Session { get; set; }

    public string StationId { get; set; } = string.Empty;

    public bool IsBuffetOnly { get; set; }

    public Guid? CustomerId { get; set; }

    public CustomerEntity? Customer { get; set; }

    public List<OpenSaleLineEntity> Lines { get; set; } = [];

    public PendingBillEntity? PendingBill { get; set; }
}

public sealed class OpenSaleLineEntity
{
    public long Id { get; set; }

    public Guid OpenSaleId { get; set; }

    public OpenSaleEntity? OpenSale { get; set; }

    public string CatalogItemId { get; set; } = string.Empty;

    public CatalogItemEntity? CatalogItem { get; set; }

    public SaleLineKind Kind { get; set; }

    public int Quantity { get; set; }

    public long UnitPriceToman { get; set; }

    public long LineTotalToman { get; set; }
}

public sealed class InvoiceCatalogLineEntity
{
    public long Id { get; set; }

    public Guid InvoiceId { get; set; }

    public InvoiceEntity? Invoice { get; set; }

    public string CatalogItemId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public SaleLineKind Kind { get; set; }

    public int Quantity { get; set; }

    public long UnitPriceToman { get; set; }

    public long LineTotalToman { get; set; }
}
