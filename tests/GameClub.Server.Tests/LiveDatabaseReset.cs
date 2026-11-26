using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Tests;

/// <summary>Reset a live out-of-process server database (windows-2022 kiosk integration).</summary>
public static class LiveDatabaseReset
{
    /// <summary>Like <see cref="S3TestHelpers.TruncateSql"/> but keeps kiosk lease signing keys (live relaunch must reuse the same key).</summary>
    public const string TruncateSqlPreserveLeaseSigningKeys = """
        TRUNCATE wallet_topup_corrections, invoice_member_effects, loyalty_hour_ledger, wallet_ledger, pending_wallet_topups, customer_members, branch_member_settings, branch_cafe_settings, close_records, shifts, invoice_catalog_lines, open_sale_lines, open_sales, catalog_items, catalog_categories RESTART IDENTITY CASCADE;
        TRUNCATE kiosk_heartbeats, kiosk_devices, kiosk_pairing_codes, customer_credentials, game_catalog_entries, game_catalog_categories, station_kiosk_profiles RESTART IDENTITY CASCADE;
        TRUNCATE game_rate_lines, game_titles RESTART IDENTITY CASCADE;
        DO $truncate$
        BEGIN
          IF to_regclass('public.station_internet_state') IS NOT NULL THEN
            EXECUTE 'TRUNCATE station_internet_state RESTART IDENTITY CASCADE';
          END IF;
        END
        $truncate$;
        TRUNCATE session_commands, rejected_card_to_card, payment_lines, pending_bills, audit_log, checkpoints, pause_intervals, play_segments, invoices, sessions, customers, rate_card_snapshot, stations, destination_cards RESTART IDENTITY CASCADE;
        TRUNCATE operator_users RESTART IDENTITY CASCADE;
        SELECT setval('invoice_number_seq', 1, false);
        UPDATE invoice_number_sequence SET "NextNumber" = 1;
        DELETE FROM rate_lines WHERE "GroupId" IN ('pc-normal', 'pc-vip') AND "PlayMode" = 'Offline';
        UPDATE rate_lines SET "HourlyToman" = 80000
          WHERE "GroupId" = 'pc-normal' AND "IsSpecialBand" = false AND "PlayMode" = 'Online' AND "PlayerCount" IS NULL;
        UPDATE rate_lines SET "HourlyToman" = 100000
          WHERE "GroupId" = 'pc-normal' AND "IsSpecialBand" = true AND "PlayMode" = 'Online' AND "PlayerCount" IS NULL;
        UPDATE rate_lines SET "HourlyToman" = 110000
          WHERE "GroupId" = 'pc-vip' AND "IsSpecialBand" = false AND "PlayMode" = 'Online' AND "PlayerCount" IS NULL;
        UPDATE rate_lines SET "HourlyToman" = 130000
          WHERE "GroupId" = 'pc-vip' AND "IsSpecialBand" = true AND "PlayMode" = 'Online' AND "PlayerCount" IS NULL;
        """;

    public static async Task ResetAsync(string? connectionString = null)
    {
        connectionString ??= Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
            ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")
            ?? throw new InvalidOperationException("GAMECLUB_DATABASE is not set.");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new AppDbContext(options);
        await db.Database.ExecuteSqlRawAsync(TruncateSqlPreserveLeaseSigningKeys);
        await DatabaseSeed.EnsureAsync(db);
        if (!await db.GameCatalogCategories.AnyAsync(c => c.Id == 2))
        {
            db.GameCatalogCategories.Add(new GameCatalogCategoryEntity { Id = 2, Name = "اختصاصی", SortOrder = 2 });
            await db.SaveChangesAsync();
        }
    }
}
