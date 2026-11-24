using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Pr51R5RoundingSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE branch_rounding ADD COLUMN IF NOT EXISTS "AllowWalletShortBalanceDebt" boolean NOT NULL DEFAULT true;
                ALTER TABLE branch_rounding ADD COLUMN IF NOT EXISTS "ApplyToWallet" boolean NOT NULL DEFAULT true;
                ALTER TABLE branch_rounding ADD COLUMN IF NOT EXISTS "RoundingEnabled" boolean NOT NULL DEFAULT true;
                ALTER TABLE branch_rounding ADD COLUMN IF NOT EXISTS "WalletMinBillToman" bigint NOT NULL DEFAULT 5000;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowWalletShortBalanceDebt",
                table: "branch_rounding");

            migrationBuilder.DropColumn(
                name: "ApplyToWallet",
                table: "branch_rounding");

            migrationBuilder.DropColumn(
                name: "RoundingEnabled",
                table: "branch_rounding");

            migrationBuilder.DropColumn(
                name: "WalletMinBillToman",
                table: "branch_rounding");
        }
    }
}