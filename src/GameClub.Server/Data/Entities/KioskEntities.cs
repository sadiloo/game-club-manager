namespace GameClub.Server.Data.Entities;

public sealed class KioskDeviceEntity
{
    public Guid Id { get; set; }

    public string StationId { get; set; } = string.Empty;

    public StationEntity? Station { get; set; }

    public string DeviceKeyHash { get; set; } = string.Empty;

  /// <summary>RSA public key (PEM) used to verify session leases on the device.</summary>
    public string LeaseVerifyPublicKeyPem { get; set; } = string.Empty;

    public DateTime PairedAtUtc { get; set; }

    public DateTime? LastSeenUtc { get; set; }

    public bool IsRevoked { get; set; }
}

public sealed class KioskPairingCodeEntity
{
    public long Id { get; set; }

    public string StationId { get; set; } = string.Empty;

    public StationEntity? Station { get; set; }

    public string Code { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? ConsumedAtUtc { get; set; }

    public Guid? ConsumedDeviceId { get; set; }
}

public sealed class CustomerCredentialEntity
{
    public Guid CustomerId { get; set; }

    public CustomerEntity? Customer { get; set; }

    public string LoginNameNormalized { get; set; } = string.Empty;

    public string LoginNameDisplay { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public int FailedLoginCount { get; set; }

    /// <summary>When true, login is refused until operator reset (F6).</summary>
    public bool LockedUntilReset { get; set; }
}

public sealed class GameCatalogCategoryEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }
}

public sealed class GameCatalogEntryEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int CategoryId { get; set; }

    public GameCatalogCategoryEntity? Category { get; set; }

    public string ExecutableOrUri { get; set; } = string.Empty;

    public string? Arguments { get; set; }

    public string? IconUrl { get; set; }

    public int SortOrder { get; set; }

    public bool Enabled { get; set; } = true;

    public string AllowedProcessNamesJson { get; set; } = "[]";

    public string GroupRestrictionsJson { get; set; } = "[]";

    public int? MinAge { get; set; }

    public long CatalogVersion { get; set; }
}

public sealed class StationKioskProfileEntity
{
    public string StationId { get; set; } = string.Empty;

    public StationEntity? Station { get; set; }

    /// <summary>When true, customer/guest play sessions are refused from the kiosk (test bench).</summary>
    public bool IsTestMode { get; set; }

    public string? NearEndMinutesJson { get; set; }

    public string? AlertSoundId { get; set; }
}

public sealed class KioskHeartbeatEntity
{
    public long Id { get; set; }

    public Guid DeviceId { get; set; }

    public KioskDeviceEntity? Device { get; set; }

    public Guid? SessionId { get; set; }

    public DateTime ReceivedUtc { get; set; }

    public long ClientMonotonicMs { get; set; }
}
