using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Step2Pr4Review1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RateCardSnapshotId",
                table: "sessions",
                type: "integer",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_RateCardSnapshotId",
                table: "sessions",
                column: "RateCardSnapshotId");

            migrationBuilder.AddForeignKey(
                name: "FK_sessions_rate_card_snapshot_RateCardSnapshotId",
                table: "sessions",
                column: "RateCardSnapshotId",
                principalTable: "rate_card_snapshot",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_sessions_station_active ON sessions ("StationId")
                WHERE "Status" IN ('Open', 'Paused');
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX ux_pause_intervals_session_open ON pause_intervals ("SessionId")
                WHERE "EndUtc" IS NULL;
                """);

            migrationBuilder.Sql("""
                CREATE SEQUENCE IF NOT EXISTS invoice_number_seq START WITH 1;
                SELECT setval(
                    'invoice_number_seq',
                    GREATEST(1, COALESCE((SELECT "NextNumber" FROM invoice_number_sequence LIMIT 1), 1)),
                    false);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_sessions_station_active;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_pause_intervals_session_open;");
            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS invoice_number_seq;");

            migrationBuilder.DropForeignKey(
                name: "FK_sessions_rate_card_snapshot_RateCardSnapshotId",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "IX_sessions_RateCardSnapshotId",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "RateCardSnapshotId",
                table: "sessions");
        }
    }
}
