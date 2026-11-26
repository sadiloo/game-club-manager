using System;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20261120140000_MvpBInstallerKioskCredentials")]
    public partial class MvpBInstallerKioskCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kiosk_credentials",
                columns: table => new
                {
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kiosk_credentials", x => x.Role);
                });

            migrationBuilder.AddColumn<int>(
                name: "KioskFreeUseIdleMinutes",
                table: "branch_cafe_settings",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.CreateTable(
                name: "kiosk_free_uses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    KioskDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorRole = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "operator")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kiosk_free_uses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_kiosk_free_uses_StationId_EndedAtUtc",
                table: "kiosk_free_uses",
                columns: new[] { "StationId", "EndedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "kiosk_free_uses");
            migrationBuilder.DropColumn(name: "KioskFreeUseIdleMinutes", table: "branch_cafe_settings");
            migrationBuilder.DropTable(name: "kiosk_credentials");
        }
    }
}