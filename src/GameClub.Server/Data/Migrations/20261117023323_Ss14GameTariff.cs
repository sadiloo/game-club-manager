using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class Ss14GameTariff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CurrentGameTitleId",
                table: "sessions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "GameHourlyRateToman",
                table: "play_segments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "GameNightHourlyRateToman",
                table: "play_segments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GameTitleId",
                table: "play_segments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "game_titles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    NameNormalized = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_titles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "game_rate_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GameTitleId = table.Column<int>(type: "integer", nullable: false),
                    PlayMode = table.Column<string>(type: "text", nullable: false),
                    PlayerCount = table.Column<int>(type: "integer", nullable: true),
                    HourlyToman = table.Column<long>(type: "bigint", nullable: false),
                    NightHourlyToman = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_game_rate_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_game_rate_lines_game_titles_GameTitleId",
                        column: x => x.GameTitleId,
                        principalTable: "game_titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sessions_CurrentGameTitleId",
                table: "sessions",
                column: "CurrentGameTitleId");

            migrationBuilder.CreateIndex(
                name: "IX_play_segments_GameTitleId",
                table: "play_segments",
                column: "GameTitleId");

            migrationBuilder.CreateIndex(
                name: "IX_game_rate_lines_GameTitleId_PlayMode_PlayerCount",
                table: "game_rate_lines",
                columns: new[] { "GameTitleId", "PlayMode", "PlayerCount" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_game_titles_NameNormalized",
                table: "game_titles",
                column: "NameNormalized",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_play_segments_game_titles_GameTitleId",
                table: "play_segments",
                column: "GameTitleId",
                principalTable: "game_titles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_sessions_game_titles_CurrentGameTitleId",
                table: "sessions",
                column: "CurrentGameTitleId",
                principalTable: "game_titles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_play_segments_game_titles_GameTitleId",
                table: "play_segments");

            migrationBuilder.DropForeignKey(
                name: "FK_sessions_game_titles_CurrentGameTitleId",
                table: "sessions");

            migrationBuilder.DropTable(
                name: "game_rate_lines");

            migrationBuilder.DropTable(
                name: "game_titles");

            migrationBuilder.DropIndex(
                name: "IX_sessions_CurrentGameTitleId",
                table: "sessions");

            migrationBuilder.DropIndex(
                name: "IX_play_segments_GameTitleId",
                table: "play_segments");

            migrationBuilder.DropColumn(
                name: "CurrentGameTitleId",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "GameHourlyRateToman",
                table: "play_segments");

            migrationBuilder.DropColumn(
                name: "GameNightHourlyRateToman",
                table: "play_segments");

            migrationBuilder.DropColumn(
                name: "GameTitleId",
                table: "play_segments");
        }
    }
}
