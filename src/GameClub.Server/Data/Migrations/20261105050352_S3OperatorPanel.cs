using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class S3OperatorPanel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultPlayMode",
                table: "stations",
                type: "text",
                nullable: false,
                defaultValue: "Offline");

            migrationBuilder.AddColumn<int>(
                name: "DefaultPlayerCount",
                table: "stations",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "BilledToUtc",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "StopReason",
                table: "invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SurchargeToman",
                table: "invoices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "VoidReason",
                table: "invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActorUserId",
                table: "audit_log",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "destination_cards",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_destination_cards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "operator_users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    PinHash = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pending_bills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FrozenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UnroundedPayableToman = table.Column<long>(type: "bigint", nullable: false),
                    RoundingToman = table.Column<long>(type: "bigint", nullable: false),
                    CollectibleToman = table.Column<long>(type: "bigint", nullable: false),
                    SurchargeToman = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    StopReason = table.Column<string>(type: "text", nullable: true),
                    RevertStateJson = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_bills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pending_bills_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payment_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Method = table.Column<string>(type: "text", nullable: false),
                    AmountToman = table.Column<long>(type: "bigint", nullable: false),
                    DestinationCardId = table.Column<string>(type: "text", nullable: true),
                    Reference = table.Column<string>(type: "text", nullable: true),
                    ConfirmationStatus = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payment_lines_destination_cards_DestinationCardId",
                        column: x => x.DestinationCardId,
                        principalTable: "destination_cards",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_payment_lines_invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rejected_card_to_card",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PaymentLineId = table.Column<long>(type: "bigint", nullable: false),
                    AmountToman = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rejected_card_to_card", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rejected_card_to_card_payment_lines_PaymentLineId",
                        column: x => x.PaymentLineId,
                        principalTable: "payment_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_operator_users_Username",
                table: "operator_users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_lines_DestinationCardId",
                table: "payment_lines",
                column: "DestinationCardId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_lines_InvoiceId",
                table: "payment_lines",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_pending_bills_SessionId",
                table: "pending_bills",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rejected_card_to_card_PaymentLineId",
                table: "rejected_card_to_card",
                column: "PaymentLineId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operator_users");

            migrationBuilder.DropTable(
                name: "pending_bills");

            migrationBuilder.DropTable(
                name: "rejected_card_to_card");

            migrationBuilder.DropTable(
                name: "payment_lines");

            migrationBuilder.DropTable(
                name: "destination_cards");

            migrationBuilder.DropColumn(
                name: "DefaultPlayMode",
                table: "stations");

            migrationBuilder.DropColumn(
                name: "DefaultPlayerCount",
                table: "stations");

            migrationBuilder.DropColumn(
                name: "BilledToUtc",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "StopReason",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "SurchargeToman",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "VoidReason",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "ActorUserId",
                table: "audit_log");
        }
    }
}
