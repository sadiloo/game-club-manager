using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class _20261108_StepPcKioskScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowPcWhileInDebt",
                table: "branch_member_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ApplyDiscountsToPc",
                table: "branch_member_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "GraceMinuteEnabled",
                table: "branch_member_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "customer_credentials",
                columns: table => new
                {
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginNameNormalized = table.Column<string>(type: "text", nullable: false),
                    LoginNameDisplay = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    FailedLoginCount = table.Column<int>(type: "integer", nullable: false),
                    LockedUntilReset = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_credentials", x => x.CustomerId);
                    table.ForeignKey(
                        name: "FK_customer_credentials_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "game_catalog_categories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_catalog_categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "kiosk_devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StationId = table.Column<string>(type: "text", nullable: false),
                    DeviceKeyHash = table.Column<string>(type: "text", nullable: false),
                    LeaseVerifyPublicKeyPem = table.Column<string>(type: "text", nullable: false),
                    PairedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kiosk_devices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_kiosk_devices_stations_StationId",
                        column: x => x.StationId,
                        principalTable: "stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kiosk_pairing_codes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StationId = table.Column<string>(type: "text", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConsumedDeviceId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kiosk_pairing_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_kiosk_pairing_codes_stations_StationId",
                        column: x => x.StationId,
                        principalTable: "stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "station_kiosk_profiles",
                columns: table => new
                {
                    StationId = table.Column<string>(type: "text", nullable: false),
                    IsTestMode = table.Column<bool>(type: "boolean", nullable: false),
                    NearEndMinutesJson = table.Column<string>(type: "text", nullable: true),
                    AlertSoundId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_station_kiosk_profiles", x => x.StationId);
                    table.ForeignKey(
                        name: "FK_station_kiosk_profiles_stations_StationId",
                        column: x => x.StationId,
                        principalTable: "stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "game_catalog_entries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CategoryId = table.Column<int>(type: "integer", nullable: false),
                    ExecutableOrUri = table.Column<string>(type: "text", nullable: false),
                    Arguments = table.Column<string>(type: "text", nullable: true),
                    IconUrl = table.Column<string>(type: "text", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    AllowedProcessNamesJson = table.Column<string>(type: "text", nullable: false),
                    GroupRestrictionsJson = table.Column<string>(type: "text", nullable: false),
                    MinAge = table.Column<int>(type: "integer", nullable: true),
                    CatalogVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_catalog_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_game_catalog_entries_game_catalog_categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "game_catalog_categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kiosk_heartbeats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReceivedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClientMonotonicMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kiosk_heartbeats", x => x.Id);
                    table.ForeignKey(
                        name: "FK_kiosk_heartbeats_kiosk_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "kiosk_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_credentials_LoginNameNormalized",
                table: "customer_credentials",
                column: "LoginNameNormalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_game_catalog_entries_CategoryId",
                table: "game_catalog_entries",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_kiosk_devices_StationId",
                table: "kiosk_devices",
                column: "StationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_kiosk_heartbeats_DeviceId",
                table: "kiosk_heartbeats",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_kiosk_pairing_codes_StationId_Code",
                table: "kiosk_pairing_codes",
                columns: new[] { "StationId", "Code" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_credentials");

            migrationBuilder.DropTable(
                name: "game_catalog_entries");

            migrationBuilder.DropTable(
                name: "kiosk_heartbeats");

            migrationBuilder.DropTable(
                name: "kiosk_pairing_codes");

            migrationBuilder.DropTable(
                name: "station_kiosk_profiles");

            migrationBuilder.DropTable(
                name: "game_catalog_categories");

            migrationBuilder.DropTable(
                name: "kiosk_devices");

            migrationBuilder.DropColumn(
                name: "AllowPcWhileInDebt",
                table: "branch_member_settings");

            migrationBuilder.DropColumn(
                name: "ApplyDiscountsToPc",
                table: "branch_member_settings");

            migrationBuilder.DropColumn(
                name: "GraceMinuteEnabled",
                table: "branch_member_settings");
        }
    }
}
