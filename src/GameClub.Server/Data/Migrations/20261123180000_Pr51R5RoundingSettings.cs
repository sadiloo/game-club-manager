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
            migrationBuilder.AddColumn<bool>(
                name: "AllowWalletShortBalanceDebt",
                table: "branch_rounding",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ApplyToWallet",
                table: "branch_rounding",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "RoundingEnabled",
                table: "branch_rounding",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<long>(
                name: "WalletMinBillToman",
                table: "branch_rounding",
                type: "bigint",
                nullable: false,
                defaultValue: 5000L);
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