using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class StepWalletLoyaltyScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PrepaidAmountBudgetToman",
                table: "sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PrepaidBookedActiveSeconds",
                table: "sessions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PrepaidEndAlertSent",
                table: "sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PrepaidKind",
                table: "sessions",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "WalletCustomerId",
                table: "payment_lines",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WalletGiftDrawnToman",
                table: "payment_lines",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "WalletRealDrawnToman",
                table: "payment_lines",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "DiscountToman",
                table: "invoices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "branch_member_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WalletDebtFloorToman = table.Column<long>(type: "bigint", nullable: true),
                    LevelLadderJson = table.Column<string>(type: "text", nullable: false),
                    BirthdayPercent = table.Column<int>(type: "integer", nullable: false),
                    BirthdayWindowKind = table.Column<string>(type: "text", nullable: false),
                    BirthdayWindowDays = table.Column<int>(type: "integer", nullable: false),
                    BirthdayWindowAnchor = table.Column<string>(type: "text", nullable: false),
                    PackageConsumptionOrder = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_branch_member_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "customer_members",
                columns: table => new
                {
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RealToman = table.Column<long>(type: "bigint", nullable: false),
                    GiftToman = table.Column<long>(type: "bigint", nullable: false),
                    BasisRealToman = table.Column<long>(type: "bigint", nullable: false),
                    BasisGiftToman = table.Column<long>(type: "bigint", nullable: false),
                    PositiveDrawnToman = table.Column<long>(type: "bigint", nullable: false),
                    GiftDrawnFromBasisToman = table.Column<long>(type: "bigint", nullable: false),
                    LifetimePlayMinutes = table.Column<long>(type: "bigint", nullable: false),
                    BirthYear = table.Column<int>(type: "integer", nullable: true),
                    BirthMonth = table.Column<int>(type: "integer", nullable: true),
                    BirthDay = table.Column<int>(type: "integer", nullable: true),
                    HasOpeningBalance = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_members", x => x.CustomerId);
                    table.ForeignKey(
                        name: "FK_customer_members_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invoice_member_effects",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    WalletRealDrawnToman = table.Column<long>(type: "bigint", nullable: false),
                    WalletGiftDrawnToman = table.Column<long>(type: "bigint", nullable: false),
                    LoyaltyMinutesPosted = table.Column<long>(type: "bigint", nullable: false),
                    PackageDrawsJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_member_effects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_invoice_member_effects_invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_hour_ledger",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeltaMinutes = table.Column<long>(type: "bigint", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_hour_ledger", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "package_products",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PriceToman = table.Column<long>(type: "bigint", nullable: false),
                    Minutes = table.Column<long>(type: "bigint", nullable: false),
                    AllowedGroupIdsJson = table.Column<string>(type: "text", nullable: true),
                    ExpiresAfterDays = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_package_products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "customer_packages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    RemainingMinutes = table.Column<long>(type: "bigint", nullable: false),
                    FaceValueToman = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AllowedGroupIdsJson = table.Column<string>(type: "text", nullable: true),
                    ValidFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HourWindowJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_packages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_customer_packages_customer_members_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "customer_members",
                        principalColumn: "CustomerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pending_wallet_topups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountToman = table.Column<long>(type: "bigint", nullable: false),
                    Method = table.Column<string>(type: "text", nullable: false),
                    DestinationCardId = table.Column<string>(type: "text", nullable: true),
                    Reference = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_wallet_topups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pending_wallet_topups_customer_members_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "customer_members",
                        principalColumn: "CustomerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wallet_ledger",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    RealDeltaToman = table.Column<long>(type: "bigint", nullable: false),
                    GiftDeltaToman = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: true),
                    Method = table.Column<string>(type: "text", nullable: true),
                    DestinationCardId = table.Column<string>(type: "text", nullable: true),
                    Reference = table.Column<string>(type: "text", nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_ledger", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wallet_ledger_customer_members_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "customer_members",
                        principalColumn: "CustomerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_packages_CustomerId",
                table: "customer_packages",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_member_effects_InvoiceId",
                table: "invoice_member_effects",
                column: "InvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pending_wallet_topups_CustomerId",
                table: "pending_wallet_topups",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_wallet_ledger_CustomerId",
                table: "wallet_ledger",
                column: "CustomerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "branch_member_settings");

            migrationBuilder.DropTable(
                name: "customer_packages");

            migrationBuilder.DropTable(
                name: "invoice_member_effects");

            migrationBuilder.DropTable(
                name: "loyalty_hour_ledger");

            migrationBuilder.DropTable(
                name: "package_products");

            migrationBuilder.DropTable(
                name: "pending_wallet_topups");

            migrationBuilder.DropTable(
                name: "wallet_ledger");

            migrationBuilder.DropTable(
                name: "customer_members");

            migrationBuilder.DropColumn(
                name: "PrepaidAmountBudgetToman",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "PrepaidBookedActiveSeconds",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "PrepaidEndAlertSent",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "PrepaidKind",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "WalletCustomerId",
                table: "payment_lines");

            migrationBuilder.DropColumn(
                name: "WalletGiftDrawnToman",
                table: "payment_lines");

            migrationBuilder.DropColumn(
                name: "WalletRealDrawnToman",
                table: "payment_lines");

            migrationBuilder.DropColumn(
                name: "DiscountToman",
                table: "invoices");
        }
    }
}
