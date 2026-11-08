using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Data;

public sealed class AuditLogAppendOnlyException() : InvalidOperationException("audit_log is append-only");

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<StationGroupEntity> StationGroups => Set<StationGroupEntity>();

    public DbSet<StationEntity> Stations => Set<StationEntity>();

    public DbSet<PlaySessionEntity> Sessions => Set<PlaySessionEntity>();

    public DbSet<PlaySegmentEntity> Segments => Set<PlaySegmentEntity>();

    public DbSet<PauseIntervalEntity> Pauses => Set<PauseIntervalEntity>();

    public DbSet<CheckpointEntity> Checkpoints => Set<CheckpointEntity>();

    public DbSet<InvoiceEntity> Invoices => Set<InvoiceEntity>();

    public DbSet<PendingBillEntity> PendingBills => Set<PendingBillEntity>();

    public DbSet<PaymentLineEntity> PaymentLines => Set<PaymentLineEntity>();

    public DbSet<DestinationCardEntity> DestinationCards => Set<DestinationCardEntity>();

    public DbSet<OperatorUserEntity> OperatorUsers => Set<OperatorUserEntity>();

    public DbSet<RejectedCardToCardEntity> RejectedCardToCard => Set<RejectedCardToCardEntity>();

    public DbSet<InvoiceNumberSequence> InvoiceNumbers => Set<InvoiceNumberSequence>();

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    public DbSet<RateCardSnapshotEntity> RateCardSnapshots => Set<RateCardSnapshotEntity>();

    public DbSet<BranchRoundingEntity> BranchRounding => Set<BranchRoundingEntity>();

    public DbSet<RateLineEntity> RateLines => Set<RateLineEntity>();

    public DbSet<CatalogCategoryEntity> CatalogCategories => Set<CatalogCategoryEntity>();

    public DbSet<CatalogItemEntity> CatalogItems => Set<CatalogItemEntity>();

    public DbSet<OpenSaleEntity> OpenSales => Set<OpenSaleEntity>();

    public DbSet<OpenSaleLineEntity> OpenSaleLines => Set<OpenSaleLineEntity>();

    public DbSet<InvoiceCatalogLineEntity> InvoiceCatalogLines => Set<InvoiceCatalogLineEntity>();

    public DbSet<CustomerEntity> Customers => Set<CustomerEntity>();

    public DbSet<BranchMemberSettingsEntity> BranchMemberSettings => Set<BranchMemberSettingsEntity>();

    public DbSet<CustomerMemberEntity> CustomerMembers => Set<CustomerMemberEntity>();

    public DbSet<WalletLedgerEntryEntity> WalletLedger => Set<WalletLedgerEntryEntity>();

    public DbSet<PendingWalletTopUpEntity> PendingWalletTopUps => Set<PendingWalletTopUpEntity>();

    public DbSet<CustomerPackageEntity> CustomerPackages => Set<CustomerPackageEntity>();

    public DbSet<PackageProductEntity> PackageProducts => Set<PackageProductEntity>();

    public DbSet<LoyaltyHourLedgerEntity> LoyaltyHourLedger => Set<LoyaltyHourLedgerEntity>();

    public DbSet<InvoiceMemberEffectEntity> InvoiceMemberEffects => Set<InvoiceMemberEffectEntity>();

    public DbSet<BranchCafeSettingsEntity> BranchCafeSettings => Set<BranchCafeSettingsEntity>();

    public DbSet<ShiftEntity> Shifts => Set<ShiftEntity>();

    public DbSet<CloseRecordEntity> CloseRecords => Set<CloseRecordEntity>();

    public DbSet<SessionCommandEntity> SessionCommands => Set<SessionCommandEntity>();

    public DbSet<KioskDeviceEntity> KioskDevices => Set<KioskDeviceEntity>();

    public DbSet<KioskPairingCodeEntity> KioskPairingCodes => Set<KioskPairingCodeEntity>();

    public DbSet<CustomerCredentialEntity> CustomerCredentials => Set<CustomerCredentialEntity>();

    public DbSet<GameCatalogCategoryEntity> GameCatalogCategories => Set<GameCatalogCategoryEntity>();

    public DbSet<GameCatalogEntryEntity> GameCatalogEntries => Set<GameCatalogEntryEntity>();

    public DbSet<StationKioskProfileEntity> StationKioskProfiles => Set<StationKioskProfileEntity>();

    public DbSet<KioskHeartbeatEntity> KioskHeartbeats => Set<KioskHeartbeatEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StationGroupEntity>(e =>
        {
            e.ToTable("station_groups");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>();
        });

        modelBuilder.Entity<StationEntity>(e =>
        {
            e.ToTable("stations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Id).IsUnique();
            e.HasOne(x => x.Group).WithMany().HasForeignKey(x => x.GroupId);
            e.Property(x => x.DefaultPlayMode).HasConversion<string>();
            e.Property(x => x.OperationalState).HasConversion<string>();
        });

        modelBuilder.Entity<CustomerEntity>(e =>
        {
            e.ToTable("customers");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Mobile).IsUnique();
            e.HasIndex(x => x.MembershipCodeKey).IsUnique();
        });

        modelBuilder.Entity<PlaySessionEntity>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Mode).HasConversion<string>();
            e.Property(x => x.PrepaidKind).HasConversion<string>();
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId);
            e.HasOne(x => x.Invoice).WithOne(x => x.Session).HasForeignKey<InvoiceEntity>(x => x.SessionId)
                .IsRequired(false);
            e.HasOne(x => x.PendingBill).WithOne(x => x.Session).HasForeignKey<PendingBillEntity>(x => x.SessionId)
                .IsRequired(false);
            e.HasOne(x => x.RateCardSnapshot).WithMany().HasForeignKey(x => x.RateCardSnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<PlaySegmentEntity>(e =>
        {
            e.ToTable("play_segments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Mode).HasConversion<string>();
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasIndex(x => new { x.SessionId, x.SegmentKey }).IsUnique();
        });

        modelBuilder.Entity<SessionCommandEntity>(e =>
        {
            e.ToTable("session_commands");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SessionId, x.CommandId }).IsUnique();
            e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId);
        });

        modelBuilder.Entity<PauseIntervalEntity>(e =>
        {
            e.ToTable("pause_intervals");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<CheckpointEntity>(e =>
        {
            e.ToTable("checkpoints");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<InvoiceEntity>(e =>
        {
            e.ToTable("invoices");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Number).IsUnique();
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasOne(x => x.OpenSale).WithMany().HasForeignKey(x => x.OpenSaleId);
            e.HasOne(x => x.MemberEffect).WithOne(x => x.Invoice).HasForeignKey<InvoiceMemberEffectEntity>(x => x.InvoiceId);
        });

        modelBuilder.Entity<PendingBillEntity>(e =>
        {
            e.ToTable("pending_bills");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.SessionId).IsUnique();
            e.HasIndex(x => x.OpenSaleId).IsUnique();
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne(x => x.OpenSale).WithOne(x => x.PendingBill).HasForeignKey<PendingBillEntity>(x => x.OpenSaleId);
        });

        modelBuilder.Entity<CatalogCategoryEntity>(e =>
        {
            e.ToTable("catalog_categories");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<CatalogItemEntity>(e =>
        {
            e.ToTable("catalog_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasOne(x => x.Category).WithMany(x => x.Items).HasForeignKey(x => x.CategoryId);
        });

        modelBuilder.Entity<OpenSaleEntity>(e =>
        {
            e.ToTable("open_sales");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.SessionId).IsUnique();
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            e.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId);
        });

        modelBuilder.Entity<OpenSaleLineEntity>(e =>
        {
            e.ToTable("open_sale_lines");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasOne(x => x.OpenSale).WithMany(x => x.Lines).HasForeignKey(x => x.OpenSaleId);
            e.HasOne(x => x.CatalogItem).WithMany().HasForeignKey(x => x.CatalogItemId);
        });

        modelBuilder.Entity<InvoiceCatalogLineEntity>(e =>
        {
            e.ToTable("invoice_catalog_lines");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasOne(x => x.Invoice).WithMany(x => x.CatalogLines).HasForeignKey(x => x.InvoiceId);
        });

        modelBuilder.Entity<PaymentLineEntity>(e =>
        {
            e.ToTable("payment_lines");
            e.HasKey(x => x.Id);
            e.Property(x => x.Method).HasConversion<string>();
            e.Property(x => x.ConfirmationStatus).HasConversion<string>();
            e.HasOne(x => x.DestinationCard).WithMany().HasForeignKey(x => x.DestinationCardId);
        });

        modelBuilder.Entity<DestinationCardEntity>(e =>
        {
            e.ToTable("destination_cards");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<OperatorUserEntity>(e =>
        {
            e.ToTable("operator_users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.Role).HasConversion<string>();
        });

        modelBuilder.Entity<RejectedCardToCardEntity>(e =>
        {
            e.ToTable("rejected_card_to_card");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.PaymentLineId).IsUnique();
        });

        modelBuilder.Entity<InvoiceNumberSequence>(e =>
        {
            e.ToTable("invoice_number_sequence");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<AuditLogEntry>(e =>
        {
            e.ToTable("audit_log");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<RateCardSnapshotEntity>(e =>
        {
            e.ToTable("rate_card_snapshot");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<BranchRoundingEntity>(e =>
        {
            e.ToTable("branch_rounding");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<RateLineEntity>(e =>
        {
            e.ToTable("rate_lines");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.GroupId, x.PlayMode, x.PlayerCount, x.IsSpecialBand }).IsUnique();
        });

        modelBuilder.Entity<BranchMemberSettingsEntity>(e =>
        {
            e.ToTable("branch_member_settings");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<CustomerMemberEntity>(e =>
        {
            e.ToTable("customer_members");
            e.HasKey(x => x.CustomerId);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        });

        modelBuilder.Entity<WalletLedgerEntryEntity>(e =>
        {
            e.ToTable("wallet_ledger");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.Method).HasConversion<string>();
            e.HasOne(x => x.Member).WithMany().HasForeignKey(x => x.CustomerId);
        });

        modelBuilder.Entity<PendingWalletTopUpEntity>(e =>
        {
            e.ToTable("pending_wallet_topups");
            e.HasKey(x => x.Id);
            e.Property(x => x.Method).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            e.HasOne(x => x.Member).WithMany().HasForeignKey(x => x.CustomerId);
        });

        modelBuilder.Entity<CustomerPackageEntity>(e =>
        {
            e.ToTable("customer_packages");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Member).WithMany().HasForeignKey(x => x.CustomerId);
        });

        modelBuilder.Entity<PackageProductEntity>(e =>
        {
            e.ToTable("package_products");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<LoyaltyHourLedgerEntity>(e =>
        {
            e.ToTable("loyalty_hour_ledger");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<InvoiceMemberEffectEntity>(e =>
        {
            e.ToTable("invoice_member_effects");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.InvoiceId).IsUnique();
            e.HasOne(x => x.Invoice).WithOne(x => x.MemberEffect).HasForeignKey<InvoiceMemberEffectEntity>(x => x.InvoiceId);
        });

        modelBuilder.Entity<BranchCafeSettingsEntity>(e =>
        {
            e.ToTable("branch_cafe_settings");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<ShiftEntity>(e =>
        {
            e.ToTable("shifts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>();
            e.HasOne(x => x.CloseRecord).WithOne(x => x.Shift).HasForeignKey<CloseRecordEntity>(x => x.ShiftId)
                .IsRequired(false);
        });

        modelBuilder.Entity<CloseRecordEntity>(e =>
        {
            e.ToTable("close_records");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ShiftId).IsUnique();
            e.HasOne(x => x.Shift).WithOne(x => x.CloseRecord).HasForeignKey<CloseRecordEntity>(x => x.ShiftId)
                .IsRequired(false);
        });

        modelBuilder.Entity<KioskDeviceEntity>(e =>
        {
            e.ToTable("kiosk_devices");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.StationId).IsUnique();
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId);
        });

        modelBuilder.Entity<KioskPairingCodeEntity>(e =>
        {
            e.ToTable("kiosk_pairing_codes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.StationId, x.Code });
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId);
        });

        modelBuilder.Entity<CustomerCredentialEntity>(e =>
        {
            e.ToTable("customer_credentials");
            e.HasKey(x => x.CustomerId);
            e.HasIndex(x => x.LoginNameNormalized).IsUnique();
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
        });

        modelBuilder.Entity<GameCatalogCategoryEntity>(e =>
        {
            e.ToTable("game_catalog_categories");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<GameCatalogEntryEntity>(e =>
        {
            e.ToTable("game_catalog_entries");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId);
        });

        modelBuilder.Entity<StationKioskProfileEntity>(e =>
        {
            e.ToTable("station_kiosk_profiles");
            e.HasKey(x => x.StationId);
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId);
        });

        modelBuilder.Entity<KioskHeartbeatEntity>(e =>
        {
            e.ToTable("kiosk_heartbeats");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId);
        });
    }

    public override int SaveChanges()
    {
        RejectAuditLogMutations();
        return base.SaveChanges();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectAuditLogMutations();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        RejectAuditLogMutations();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RejectAuditLogMutations();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void RejectAuditLogMutations()
    {
        foreach (var entry in ChangeTracker.Entries<AuditLogEntry>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new AuditLogAppendOnlyException();
            }
        }
    }
}
