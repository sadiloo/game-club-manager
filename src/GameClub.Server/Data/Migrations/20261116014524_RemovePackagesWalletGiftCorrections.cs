using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovePackagesWalletGiftCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $guard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM customer_packages LIMIT 1) THEN
                        RAISE EXCEPTION 'RemovePackagesWalletGiftCorrections: customer_packages has rows';
                    END IF;
                    IF EXISTS (SELECT 1 FROM wallet_ledger WHERE "Kind" = 'PackagePurchase' LIMIT 1) THEN
                        RAISE EXCEPTION 'RemovePackagesWalletGiftCorrections: wallet_ledger has PackagePurchase rows';
                    END IF;
                END $guard$;
                """);

            migrationBuilder.DropTable(
                name: "customer_packages");

            migrationBuilder.DropTable(
                name: "package_products");

            migrationBuilder.DropColumn(
                name: "PackageDrawsJson",
                table: "invoice_member_effects");

            migrationBuilder.DropColumn(
                name: "PackageConsumptionOrder",
                table: "branch_member_settings");

            migrationBuilder.AddColumn<bool>(
                name: "IsPostCloseAdjustment",
                table: "wallet_ledger",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TopUpGroupId",
                table: "wallet_ledger",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "GiftToman",
                table: "pending_wallet_topups",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "OperatorGiftMaxPercent",
                table: "branch_member_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "OperatorGiftMaxToman",
                table: "branch_member_settings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "wallet_topup_corrections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TopUpGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrectionLedgerEntryId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Method = table.Column<string>(type: "text", nullable: false),
                    BeforeAmountToman = table.Column<long>(type: "bigint", nullable: false),
                    AfterAmountToman = table.Column<long>(type: "bigint", nullable: false),
                    BeforeGiftToman = table.Column<long>(type: "bigint", nullable: false),
                    AfterGiftToman = table.Column<long>(type: "bigint", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginalShiftId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsPostCloseAdjustment = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_topup_corrections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_wallet_topup_corrections_AtUtc",
                table: "wallet_topup_corrections",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_wallet_topup_corrections_TopUpGroupId",
                table: "wallet_topup_corrections",
                column: "TopUpGroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wallet_topup_corrections");

            migrationBuilder.DropColumn(
                name: "IsPostCloseAdjustment",
                table: "wallet_ledger");

            migrationBuilder.DropColumn(
                name: "TopUpGroupId",
                table: "wallet_ledger");

            migrationBuilder.DropColumn(
                name: "GiftToman",
                table: "pending_wallet_topups");

            migrationBuilder.DropColumn(
                name: "OperatorGiftMaxPercent",
                table: "branch_member_settings");

            migrationBuilder.DropColumn(
                name: "OperatorGiftMaxToman",
                table: "branch_member_settings");

            migrationBuilder.AddColumn<string>(
                name: "PackageDrawsJson",
                table: "invoice_member_effects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackageConsumptionOrder",
                table: "branch_member_settings",
                type: "text",
                nullable: false,
                defaultValue: "packages_then_money");

            migrationBuilder.CreateTable(
                name: "customer_packages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowedGroupIdsJson = table.Column<string>(type: "text", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FaceValueToman = table.Column<long>(type: "bigint", nullable: false),
                    HourWindowJson = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PurchaseInvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    RemainingMinutes = table.Column<long>(type: "bigint", nullable: false),
                    ValidFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ValidToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                name: "package_products",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    AllowedGroupIdsJson = table.Column<string>(type: "text", nullable: true),
                    ExpiresAfterDays = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Minutes = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PriceToman = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_package_products", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_packages_CustomerId",
                table: "customer_packages",
                column: "CustomerId");
        }
    }
}
