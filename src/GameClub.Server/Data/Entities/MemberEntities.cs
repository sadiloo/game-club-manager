using GameClub.Billing;

namespace GameClub.Server.Data.Entities;

public enum WalletLedgerKind
{
    TopUpReal = 0,
    TopUpGift = 1,
    InvoiceSpend = 2,
    InvoiceVoidReversal = 3,
    OpeningBalance = 4,
    Correction = 5,
}

public enum PendingWalletTopUpStatus
{
    Pending = 0,
    Confirmed = 1,
    Rejected = 2,
}

public enum PrepaidSessionKind
{
    None = 0,
    Duration = 1,
    Amount = 2,
}

/// <summary>Branch-wide member wallet, loyalty, and birthday settings (singleton row id = 1).</summary>
public sealed class BranchMemberSettingsEntity
{
    public int Id { get; set; } = 1;

    /// <summary>Null = no debt permitted; negative value is the real-money floor (e.g. -50_000).</summary>
    public long? WalletDebtFloorToman { get; set; }

    public string LevelLadderJson { get; set; } = "[]";

    public int BirthdayPercent { get; set; } = 10;

    /// <summary>exact_day | birth_month | day_window</summary>
    public string BirthdayWindowKind { get; set; } = "birth_month";

    public int BirthdayWindowDays { get; set; } = 1;

    /// <summary>on_and_after | around</summary>
    public string BirthdayWindowAnchor { get; set; } = "on_and_after";

    public long OperatorGiftMaxToman { get; set; }

    public int OperatorGiftMaxPercent { get; set; }

    public int MaxCombinedDiscountPercent { get; set; } = 100;

    public bool AllowPcWhileInDebt { get; set; }

    public bool ApplyDiscountsToPc { get; set; }

    public bool GraceMinuteEnabled { get; set; } = true;
}

public sealed class CustomerMemberEntity
{
    public Guid CustomerId { get; set; }

    public CustomerEntity? Customer { get; set; }

    public long RealToman { get; set; }

    public long GiftToman { get; set; }

    public long BasisRealToman { get; set; }

    public long BasisGiftToman { get; set; }

    public long PositiveDrawnToman { get; set; }

    public long GiftDrawnFromBasisToman { get; set; }

    /// <summary>Lifetime billed play minutes (remainder kept).</summary>
    public long LifetimePlayMinutes { get; set; }

    public int? BirthYear { get; set; }

    public int? BirthMonth { get; set; }

    public int? BirthDay { get; set; }

    public bool HasOpeningBalance { get; set; }
}

public sealed class WalletLedgerEntryEntity
{
    public long Id { get; set; }

    public Guid CustomerId { get; set; }

    public CustomerMemberEntity? Member { get; set; }

    public DateTime AtUtc { get; set; }

    public WalletLedgerKind Kind { get; set; }

    public long RealDeltaToman { get; set; }

    public long GiftDeltaToman { get; set; }

    public string? Label { get; set; }

    public PaymentMethod? Method { get; set; }

    public string? DestinationCardId { get; set; }

    public string? Reference { get; set; }

    public Guid? InvoiceId { get; set; }

    public Guid? ActorUserId { get; set; }

    public Guid? ShiftId { get; set; }

    public int? WorkingDayYear { get; set; }

    public int? WorkingDayMonth { get; set; }

    public int? WorkingDayDay { get; set; }

    public Guid? TopUpGroupId { get; set; }

    public bool IsPostCloseAdjustment { get; set; }
}

public sealed class PendingWalletTopUpEntity
{
    public long Id { get; set; }

    public Guid CustomerId { get; set; }

    public CustomerMemberEntity? Member { get; set; }

    public long AmountToman { get; set; }

    public long GiftToman { get; set; }

    public PaymentMethod Method { get; set; }

    public string? DestinationCardId { get; set; }

    public string? Reference { get; set; }

    public PendingWalletTopUpStatus Status { get; set; } = PendingWalletTopUpStatus.Pending;

    public DateTime CreatedAtUtc { get; set; }

    public Guid? ActorUserId { get; set; }

    public Guid? ShiftId { get; set; }
}

public sealed class WalletCommandEntity
{
    public long Id { get; set; }

    public Guid CommandId { get; set; }

    public string CommandType { get; set; } = string.Empty;

    public Guid CustomerId { get; set; }

    public string RequestFingerprint { get; set; } = string.Empty;

    public Guid? TopUpGroupId { get; set; }

    public long? PendingWalletTopUpId { get; set; }

    public string? ResultJson { get; set; }

    public DateTime AppliedAtUtc { get; set; }
}

public sealed class WalletTopUpCorrectionEntity
{
    public long Id { get; set; }

    public Guid? CommandId { get; set; }

    public Guid TopUpGroupId { get; set; }

    public long CorrectionLedgerEntryId { get; set; }

    public Guid CustomerId { get; set; }

    public PaymentMethod Method { get; set; }

    public long BeforeAmountToman { get; set; }

    public long AfterAmountToman { get; set; }

    public long BeforeGiftToman { get; set; }

    public long AfterGiftToman { get; set; }

    public string Description { get; set; } = string.Empty;

    public Guid ActorUserId { get; set; }

    public DateTime AtUtc { get; set; }

    public Guid? ShiftId { get; set; }

    public Guid? OriginalShiftId { get; set; }

    public bool IsPostCloseAdjustment { get; set; }
}

public sealed class LoyaltyHourLedgerEntity
{
    public long Id { get; set; }

    public Guid CustomerId { get; set; }

    public DateTime AtUtc { get; set; }

    public long DeltaMinutes { get; set; }

    public Guid? InvoiceId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public Guid? ActorUserId { get; set; }
}

public sealed class InvoiceMemberEffectEntity
{
    public long Id { get; set; }

    public Guid InvoiceId { get; set; }

    public InvoiceEntity? Invoice { get; set; }

    public Guid CustomerId { get; set; }

    public long WalletRealDrawnToman { get; set; }

    public long WalletGiftDrawnToman { get; set; }

    public long LoyaltyMinutesPosted { get; set; }

}
