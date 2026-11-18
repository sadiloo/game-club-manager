using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameClub.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class PcInternetRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "station_internet_state",
                columns: table => new
                {
                    StationId = table.Column<string>(type: "text", nullable: false),
                    Epoch = table.Column<Guid>(type: "uuid", nullable: false),
                    DesiredOn = table.Column<bool>(type: "boolean", nullable: false),
                    DesiredVersion = table.Column<long>(type: "bigint", nullable: false),
                    DesiredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DesiredByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DesiredReason = table.Column<string>(type: "text", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppliedVersion = table.Column<long>(type: "bigint", nullable: true),
                    AppliedOn = table.Column<bool>(type: "boolean", nullable: true),
                    AppliedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    LastReportUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_station_internet_state", x => x.StationId);
                    table.ForeignKey(
                        name: "FK_station_internet_state_stations_StationId",
                        column: x => x.StationId,
                        principalTable: "stations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                """
                UPDATE rate_lines
                SET "PlayMode" = 'Online', "PlayerCount" = NULL
                WHERE "GroupId" IN ('pc-normal', 'pc-vip')
                  AND "PlayMode" = 'Offline'
                  AND "PlayerCount" = 1
                  AND "IsSpecialBand" = false;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO rate_lines ("GroupId", "PlayMode", "PlayerCount", "IsSpecialBand", "HourlyToman")
                SELECT d."GroupId", 'Online', NULL, true, d."HourlyToman" + br."PcSpecialBandSurchargeToman"
                FROM rate_lines d
                CROSS JOIN branch_rounding br
                WHERE d."GroupId" IN ('pc-normal', 'pc-vip')
                  AND d."PlayMode" = 'Online'
                  AND d."PlayerCount" IS NULL
                  AND d."IsSpecialBand" = false
                  AND NOT EXISTS (
                    SELECT 1 FROM rate_lines n
                    WHERE n."GroupId" = d."GroupId"
                      AND n."PlayMode" = 'Online'
                      AND n."IsSpecialBand" = true);
                """);

            migrationBuilder.DropColumn(
                name: "PcSpecialBandSurchargeToman",
                table: "branch_rounding");

            migrationBuilder.Sql(
                """
                UPDATE stations s
                SET "DefaultPlayMode" = 'Online'
                FROM station_groups g
                WHERE s."GroupId" = g."Id" AND g."Kind" = 'Pc';
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO station_internet_state (
                    "StationId", "Epoch", "DesiredOn", "DesiredVersion", "DesiredAtUtc", "DesiredReason")
                SELECT s."Id", gen_random_uuid(), true, 0, NOW() AT TIME ZONE 'UTC', 'session_start'
                FROM stations s
                INNER JOIN station_groups g ON s."GroupId" = g."Id"
                WHERE g."Kind" = 'Pc';
                """);

            migrationBuilder.Sql(
                """
                UPDATE sessions sess
                SET "Mode" = 'Online'
                FROM stations st
                INNER JOIN station_groups g ON st."GroupId" = g."Id"
                WHERE sess."StationId" = st."Id"
                  AND g."Kind" = 'Pc'
                  AND sess."Status" <> 'Closed';
                """);

            migrationBuilder.Sql(
                """
                UPDATE play_segments seg
                SET "Mode" = 'Online', "PlayerCount" = 1
                FROM sessions sess
                INNER JOIN stations st ON sess."StationId" = st."Id"
                INNER JOIN station_groups g ON st."GroupId" = g."Id"
                WHERE seg."SessionId" = sess."Id"
                  AND g."Kind" = 'Pc'
                  AND sess."Status" <> 'Closed';
                """);

            migrationBuilder.Sql(
                """
                UPDATE rate_card_snapshot rcs
                SET "Json" = snap.new_json
                FROM (
                    SELECT DISTINCT ON (s."RateCardSnapshotId") s."RateCardSnapshotId" AS id,
                        (json_build_object(
                            'SchemaVersion', 2,
                            'StepToman', br."StepToman",
                            'Direction', br."Direction",
                            'Lines', COALESCE((
                                SELECT json_agg(json_build_object(
                                    'GroupId', rl."GroupId",
                                    'PlayMode', rl."PlayMode",
                                    'PlayerCount', rl."PlayerCount",
                                    'IsSpecialBand', rl."IsSpecialBand",
                                    'HourlyToman', rl."HourlyToman"
                                ) ORDER BY rl."Id")
                                FROM rate_lines rl
                            ), '[]'::json)
                        ))::text AS new_json
                    FROM sessions s
                    INNER JOIN stations st ON s."StationId" = st."Id"
                    INNER JOIN station_groups g ON st."GroupId" = g."Id"
                    CROSS JOIN branch_rounding br
                    WHERE g."Kind" = 'Pc' AND s."Status" <> 'Closed'
                ) snap
                WHERE rcs."Id" = snap.id;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO audit_log ("AtUtc", "Action", "DetailsJson", "ActorUserId")
                SELECT NOW() AT TIME ZONE 'UTC', 'rate.migrate_pc_online',
                    json_build_object(
                        'groupId', rl."GroupId",
                        'onlineDay', day."HourlyToman",
                        'onlineNight', rl."HourlyToman"
                    )::text,
                    NULL
                FROM rate_lines rl
                INNER JOIN rate_lines day ON day."GroupId" = rl."GroupId"
                    AND day."PlayMode" = 'Online' AND day."PlayerCount" IS NULL AND day."IsSpecialBand" = false
                WHERE rl."GroupId" IN ('pc-normal', 'pc-vip')
                  AND rl."PlayMode" = 'Online' AND rl."IsSpecialBand" = true;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "station_internet_state");

            migrationBuilder.AddColumn<long>(
                name: "PcSpecialBandSurchargeToman",
                table: "branch_rounding",
                type: "bigint",
                nullable: false,
                defaultValue: 20_000L);

            migrationBuilder.Sql(
                """
                UPDATE rate_lines
                SET "PlayMode" = 'Offline', "PlayerCount" = 1
                WHERE "GroupId" IN ('pc-normal', 'pc-vip')
                  AND "PlayMode" = 'Online'
                  AND "PlayerCount" IS NULL
                  AND "IsSpecialBand" = false;

                DELETE FROM rate_lines
                WHERE "GroupId" IN ('pc-normal', 'pc-vip')
                  AND "PlayMode" = 'Online'
                  AND "IsSpecialBand" = true;
                """);
        }
    }
}