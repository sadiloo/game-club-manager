using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Step2ServerScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "stations",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_stations_id_ci" ON stations (LOWER("Id"));""");

            migrationBuilder.CreateTable(
                name: "branch_rounding",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StepToman = table.Column<long>(type: "bigint", nullable: false),
                    Direction = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_branch_rounding", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rate_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GroupId = table.Column<string>(type: "text", nullable: false),
                    PlayMode = table.Column<string>(type: "text", nullable: false),
                    PlayerCount = table.Column<int>(type: "integer", nullable: true),
                    IsSpecialBand = table.Column<bool>(type: "boolean", nullable: false),
                    HourlyToman = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_lines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rate_lines_GroupId_PlayMode_PlayerCount_IsSpecialBand",
                table: "rate_lines",
                columns: new[] { "GroupId", "PlayMode", "PlayerCount", "IsSpecialBand" },
                unique: true);

            migrationBuilder.InsertData(
                table: "branch_rounding",
                columns: new[] { "Id", "StepToman", "Direction" },
                values: new object[] { 1, 5000L, "Up" });

            SeedPilotRates(migrationBuilder);
        }

        private static void SeedPilotRates(MigrationBuilder migrationBuilder)
        {
            void Line(string groupId, string mode, int? players, bool special, long toman)
            {
                migrationBuilder.Sql($"""
                    INSERT INTO rate_lines ("GroupId", "PlayMode", "PlayerCount", "IsSpecialBand", "HourlyToman")
                    VALUES ('{groupId}', '{mode}', {(players.HasValue ? players.Value.ToString() : "NULL")}, {special.ToString().ToLowerInvariant()}, {toman});
                    """);
            }

            var ps5 = "ps5";
            Line(ps5, "Offline", 1, false, 110_000);
            Line(ps5, "Offline", 2, false, 125_000);
            Line(ps5, "Offline", 3, false, 160_000);
            Line(ps5, "Offline", 4, false, 220_000);
            Line(ps5, "Online", null, false, 170_000);
            Line(ps5, "Offline", 1, true, 160_000);
            Line(ps5, "Offline", 2, true, 175_000);
            Line(ps5, "Offline", 3, true, 210_000);
            Line(ps5, "Offline", 4, true, 270_000);
            Line(ps5, "Online", null, true, 220_000);
            Line("pc-normal", "Offline", 1, false, 80_000);
            Line("pc-vip", "Offline", 1, false, 110_000);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_stations_id_ci";""");
            migrationBuilder.DropTable(
                name: "branch_rounding");

            migrationBuilder.DropTable(
                name: "rate_lines");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "stations");
        }
    }
}
