using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class S3bBuffetInventoryServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_invoices_sessions_SessionId",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_pending_bills_sessions_SessionId",
                table: "pending_bills");

            migrationBuilder.AlterColumn<Guid>(
                name: "SessionId",
                table: "pending_bills",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<long>(
                name: "BuffetGrossToman",
                table: "pending_bills",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "OpenSaleId",
                table: "pending_bills",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ServicesGrossToman",
                table: "pending_bills",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TimeGrossToman",
                table: "pending_bills",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AlterColumn<Guid>(
                name: "SessionId",
                table: "invoices",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<long>(
                name: "BuffetGrossToman",
                table: "invoices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "OpenSaleId",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ServicesGrossToman",
                table: "invoices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "TimeGrossToman",
                table: "invoices",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "catalog_categories",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "invoice_catalog_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CatalogItemId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPriceToman = table.Column<long>(type: "bigint", nullable: false),
                    LineTotalToman = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_catalog_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_invoice_catalog_lines_invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "open_sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    StationId = table.Column<string>(type: "text", nullable: false),
                    IsBuffetOnly = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_open_sales", x => x.Id);
                    table.ForeignKey(
                        name: "FK_open_sales_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "sessions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "catalog_items",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    CategoryId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    UnitPriceToman = table.Column<long>(type: "bigint", nullable: false),
                    StockQuantity = table.Column<int>(type: "integer", nullable: true),
                    LowStockWarningThreshold = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_catalog_items_catalog_categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "catalog_categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "open_sale_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OpenSaleId = table.Column<Guid>(type: "uuid", nullable: false),
                    CatalogItemId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPriceToman = table.Column<long>(type: "bigint", nullable: false),
                    LineTotalToman = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_open_sale_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_open_sale_lines_catalog_items_CatalogItemId",
                        column: x => x.CatalogItemId,
                        principalTable: "catalog_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_open_sale_lines_open_sales_OpenSaleId",
                        column: x => x.OpenSaleId,
                        principalTable: "open_sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_bills_OpenSaleId",
                table: "pending_bills",
                column: "OpenSaleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_OpenSaleId",
                table: "invoices",
                column: "OpenSaleId");

            migrationBuilder.CreateIndex(
                name: "IX_catalog_items_CategoryId",
                table: "catalog_items",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_catalog_lines_InvoiceId",
                table: "invoice_catalog_lines",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_open_sale_lines_CatalogItemId",
                table: "open_sale_lines",
                column: "CatalogItemId");

            migrationBuilder.CreateIndex(
                name: "IX_open_sale_lines_OpenSaleId",
                table: "open_sale_lines",
                column: "OpenSaleId");

            migrationBuilder.CreateIndex(
                name: "IX_open_sales_SessionId",
                table: "open_sales",
                column: "SessionId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_open_sales_OpenSaleId",
                table: "invoices",
                column: "OpenSaleId",
                principalTable: "open_sales",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_sessions_SessionId",
                table: "invoices",
                column: "SessionId",
                principalTable: "sessions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_pending_bills_open_sales_OpenSaleId",
                table: "pending_bills",
                column: "OpenSaleId",
                principalTable: "open_sales",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_pending_bills_sessions_SessionId",
                table: "pending_bills",
                column: "SessionId",
                principalTable: "sessions",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_invoices_open_sales_OpenSaleId",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_invoices_sessions_SessionId",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_pending_bills_open_sales_OpenSaleId",
                table: "pending_bills");

            migrationBuilder.DropForeignKey(
                name: "FK_pending_bills_sessions_SessionId",
                table: "pending_bills");

            migrationBuilder.DropTable(
                name: "invoice_catalog_lines");

            migrationBuilder.DropTable(
                name: "open_sale_lines");

            migrationBuilder.DropTable(
                name: "catalog_items");

            migrationBuilder.DropTable(
                name: "open_sales");

            migrationBuilder.DropTable(
                name: "catalog_categories");

            migrationBuilder.DropIndex(
                name: "IX_pending_bills_OpenSaleId",
                table: "pending_bills");

            migrationBuilder.DropIndex(
                name: "IX_invoices_OpenSaleId",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "BuffetGrossToman",
                table: "pending_bills");

            migrationBuilder.DropColumn(
                name: "OpenSaleId",
                table: "pending_bills");

            migrationBuilder.DropColumn(
                name: "ServicesGrossToman",
                table: "pending_bills");

            migrationBuilder.DropColumn(
                name: "TimeGrossToman",
                table: "pending_bills");

            migrationBuilder.DropColumn(
                name: "BuffetGrossToman",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "OpenSaleId",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "ServicesGrossToman",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "TimeGrossToman",
                table: "invoices");

            migrationBuilder.AlterColumn<Guid>(
                name: "SessionId",
                table: "pending_bills",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SessionId",
                table: "invoices",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_sessions_SessionId",
                table: "invoices",
                column: "SessionId",
                principalTable: "sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_pending_bills_sessions_SessionId",
                table: "pending_bills",
                column: "SessionId",
                principalTable: "sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
