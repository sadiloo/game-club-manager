using System;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(AppDbContext))]
    [Migration("20261107200000_StepShiftReportsScope")]
    public partial class StepShiftReportsScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "branch_cafe_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ShiftsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MultiOperatorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_branch_cafe_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "shifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OpeningFloatToman = table.Column<long>(type: "bigint", nullable: false),
                    OpenedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shifts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "close_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: true),
                    PeriodOpenedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WorkingDayYear = table.Column<int>(type: "integer", nullable: false),
                    WorkingDayMonth = table.Column<int>(type: "integer", nullable: false),
                    WorkingDayDay = table.Column<int>(type: "integer", nullable: false),
                    ActualCashToman = table.Column<long>(type: "bigint", nullable: false),
                    ActualCardReaderToman = table.Column<long>(type: "bigint", nullable: false),
                    DiscrepancyExplanation = table.Column<string>(type: "text", nullable: true),
                    NextRecipientUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_close_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_close_records_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "shifts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_close_records_ShiftId",
                table: "close_records",
                column: "ShiftId",
                unique: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ShiftId",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CloseRecordId",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkingDayYear",
                table: "invoices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkingDayMonth",
                table: "invoices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkingDayDay",
                table: "invoices",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SettledByUserId",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperatorNote",
                table: "invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OpenShiftId",
                table: "sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CarriedAtCloseRecordId",
                table: "sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ShiftId",
                table: "wallet_ledger",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkingDayYear",
                table: "wallet_ledger",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkingDayMonth",
                table: "wallet_ledger",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkingDayDay",
                table: "wallet_ledger",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ShiftId",
                table: "pending_wallet_topups",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_ShiftId",
                table: "invoices",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_CloseRecordId",
                table: "invoices",
                column: "CloseRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_OpenShiftId",
                table: "sessions",
                column: "OpenShiftId");

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_close_records_CloseRecordId",
                table: "invoices",
                column: "CloseRecordId",
                principalTable: "close_records",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_shifts_ShiftId",
                table: "invoices",
                column: "ShiftId",
                principalTable: "shifts",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_sessions_shifts_OpenShiftId",
                table: "sessions",
                column: "OpenShiftId",
                principalTable: "shifts",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_invoices_close_records_CloseRecordId",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_invoices_shifts_ShiftId",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_sessions_shifts_OpenShiftId",
                table: "sessions");

            migrationBuilder.DropTable(
                name: "close_records");

            migrationBuilder.DropTable(
                name: "branch_cafe_settings");

            migrationBuilder.DropTable(
                name: "shifts");

            migrationBuilder.DropIndex(
                name: "IX_invoices_ShiftId",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_invoices_CloseRecordId",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_sessions_OpenShiftId",
                table: "sessions");

            migrationBuilder.DropColumn(name: "ShiftId", table: "invoices");
            migrationBuilder.DropColumn(name: "CloseRecordId", table: "invoices");
            migrationBuilder.DropColumn(name: "WorkingDayYear", table: "invoices");
            migrationBuilder.DropColumn(name: "WorkingDayMonth", table: "invoices");
            migrationBuilder.DropColumn(name: "WorkingDayDay", table: "invoices");
            migrationBuilder.DropColumn(name: "SettledByUserId", table: "invoices");
            migrationBuilder.DropColumn(name: "OperatorNote", table: "invoices");
            migrationBuilder.DropColumn(name: "OpenShiftId", table: "sessions");
            migrationBuilder.DropColumn(name: "CarriedAtCloseRecordId", table: "sessions");
            migrationBuilder.DropColumn(name: "ShiftId", table: "wallet_ledger");
            migrationBuilder.DropColumn(name: "WorkingDayYear", table: "wallet_ledger");
            migrationBuilder.DropColumn(name: "WorkingDayMonth", table: "wallet_ledger");
            migrationBuilder.DropColumn(name: "WorkingDayDay", table: "wallet_ledger");
            migrationBuilder.DropColumn(name: "ShiftId", table: "pending_wallet_topups");
        }
    }
}
