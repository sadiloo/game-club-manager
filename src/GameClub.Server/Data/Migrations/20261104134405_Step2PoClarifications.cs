using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Step2PoClarifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "NetBeforeRoundingToman",
                table: "invoices",
                newName: "UnroundedPayableToman");

            migrationBuilder.RenameColumn(
                name: "PayableToman",
                table: "invoices",
                newName: "CollectibleToman");

            migrationBuilder.AddColumn<long>(
                name: "PcSpecialBandSurchargeToman",
                table: "branch_rounding",
                type: "bigint",
                nullable: false,
                defaultValue: 20000L);

            migrationBuilder.Sql("""
                DELETE FROM rate_lines
                WHERE "GroupId" IN ('pc-normal', 'pc-vip') AND "IsSpecialBand" = true;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PcSpecialBandSurchargeToman",
                table: "branch_rounding");

            migrationBuilder.RenameColumn(
                name: "CollectibleToman",
                table: "invoices",
                newName: "PayableToman");

            migrationBuilder.RenameColumn(
                name: "UnroundedPayableToman",
                table: "invoices",
                newName: "NetBeforeRoundingToman");
        }
    }
}
