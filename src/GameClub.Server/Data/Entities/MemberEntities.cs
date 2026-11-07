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
    PackagePurchase = 6,
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

    /// <summary>packages_then_money | money_then_packages</summary>
    public string PackageConsumptionOrder { get; set; } = "packages_then_money";

    public int MaxCombinedDiscountPercent { get; set; } = 100;
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
}

public sealed class PendingWalletTopUpEntity
{
    public long Id { get; set; }

    public Guid CustomerId { get; set; }

    public CustomerMemberEntity? Member { get; set; }

    public long AmountToman { get; set; }

    public PaymentMethod Method { get; set; }

    public string? DestinationCardId { get; set; }

    public string? Reference { get; set; }

    public PendingWalletTopUpStatus Status { get; set; } = PendingWalletTopUpStatus.Pending;

    public DateTime CreatedAtUtc { get; set; }

    public Guid? ActorUserId { get; set; }
}

public sealed class CustomerPackageEntity
{
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }

    public CustomerMemberEntity? Member { get; set; }

    public string Name { get; set; } = string.Empty;

    public long RemainingMinutes { get; set; }

    public long FaceValueToman { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    public string? AllowedGroupIdsJson { get; set; }

    public DateTime? ValidFromUtc { get; set; }

    public DateTime? ValidToUtc { get; set; }

    public string? HourWindowJson { get; set; }

    public Guid? PurchaseInvoiceId { get; set; }
}

public sealed class PackageProductEntity
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public long PriceToman { get; set; }

    public long Minutes { get; set; }

    public string? AllowedGroupIdsJson { get; set; }

    public int? ExpiresAfterDays { get; set; }

    public bool IsActive { get; set; } = true;
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

    public string? PackageDrawsJson { get; set; }
}
