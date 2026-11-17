using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class StepPr37FixR1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CommandId",
                table: "wallet_topup_corrections",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "wallet_commands",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommandType = table.Column<string>(type: "text", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestFingerprint = table.Column<string>(type: "text", nullable: false),
                    TopUpGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    PendingWalletTopUpId = table.Column<long>(type: "bigint", nullable: true),
                    ResultJson = table.Column<string>(type: "text", nullable: true),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_commands", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_wallet_topup_corrections_CommandId",
                table: "wallet_topup_corrections",
                column: "CommandId",
                unique: true,
                filter: "\"CommandId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_wallet_commands_CommandId",
                table: "wallet_commands",
                column: "CommandId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wallet_commands");

            migrationBuilder.DropIndex(
                name: "UX_wallet_topup_corrections_CommandId",
                table: "wallet_topup_corrections");

            migrationBuilder.DropColumn(
                name: "CommandId",
                table: "wallet_topup_corrections");
        }
    }
}
