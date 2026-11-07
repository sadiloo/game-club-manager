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
        });

        modelBuilder.Entity<PlaySessionEntity>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Mode).HasConversion<string>();
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId);
            e.HasOne(x => x.Invoice).WithOne(x => x.Session).HasForeignKey<InvoiceEntity>(x => x.SessionId)
                .IsRequired(false);
            e.HasOne(x => x.PendingBill).WithOne(x => x.Session).HasForeignKey<PendingBillEntity>(x => x.SessionId)
                .IsRequired(false);
            e.HasOne(x => x.RateCardSnapshot).WithMany().HasForeignKey(x => x.RateCardSnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlaySegmentEntity>(e =>
        {
            e.ToTable("play_segments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Mode).HasConversion<string>();
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasIndex(x => new { x.SessionId, x.SegmentKey }).IsUnique();
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
            e.HasOne(x => x.OpenSale).WithMany().HasForeignKey(x => x.OpenSaleId);
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
