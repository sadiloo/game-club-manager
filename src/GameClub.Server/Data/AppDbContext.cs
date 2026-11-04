using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<StationGroupEntity> StationGroups => Set<StationGroupEntity>();

    public DbSet<StationEntity> Stations => Set<StationEntity>();

    public DbSet<PlaySessionEntity> Sessions => Set<PlaySessionEntity>();

    public DbSet<PlaySegmentEntity> Segments => Set<PlaySegmentEntity>();

    public DbSet<PauseIntervalEntity> Pauses => Set<PauseIntervalEntity>();

    public DbSet<CheckpointEntity> Checkpoints => Set<CheckpointEntity>();

    public DbSet<InvoiceEntity> Invoices => Set<InvoiceEntity>();

    public DbSet<InvoiceNumberSequence> InvoiceNumbers => Set<InvoiceNumberSequence>();

    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();

    public DbSet<RateCardSnapshotEntity> RateCardSnapshots => Set<RateCardSnapshotEntity>();

    public DbSet<BranchRoundingEntity> BranchRounding => Set<BranchRoundingEntity>();

    public DbSet<RateLineEntity> RateLines => Set<RateLineEntity>();

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
        });

        modelBuilder.Entity<PlaySessionEntity>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Mode).HasConversion<string>();
            e.HasOne(x => x.Station).WithMany().HasForeignKey(x => x.StationId);
            e.HasOne(x => x.Invoice).WithOne(x => x.Session!).HasForeignKey<InvoiceEntity>(x => x.SessionId);
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
}
