using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Cu15QuickCustomerScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "open_sales",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Mobile = table.Column<string>(type: "text", nullable: false),
                    MembershipCode = table.Column<string>(type: "text", nullable: true),
                    MembershipCodeKey = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_CustomerId",
                table: "sessions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_open_sales_CustomerId",
                table: "open_sales",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_CustomerId",
                table: "invoices",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_customers_MembershipCodeKey",
                table: "customers",
                column: "MembershipCodeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customers_Mobile",
                table: "customers",
                column: "Mobile",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_customers_CustomerId",
                table: "invoices",
                column: "CustomerId",
                principalTable: "customers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_open_sales_customers_CustomerId",
                table: "open_sales",
                column: "CustomerId",
                principalTable: "customers",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_sessions_customers_CustomerId",
                table: "sessions",
                column: "CustomerId",
                principalTable: "customers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_invoices_customers_CustomerId",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_open_sales_customers_CustomerId",
                table: "open_sales");

            migrationBuilder.DropForeignKey(
                name: "FK_sessions_customers_CustomerId",
                table: "sessions");

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropIndex(
                name: "IX_sessions_CustomerId",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "IX_open_sales_CustomerId",
                table: "open_sales");

            migrationBuilder.DropIndex(
                name: "IX_invoices_CustomerId",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "open_sales");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "invoices");
        }
    }
}
