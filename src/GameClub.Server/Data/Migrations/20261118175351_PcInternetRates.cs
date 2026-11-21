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
                CREATE OR REPLACE FUNCTION rewrite_pc_open_snapshot_v2(old_json text) RETURNS text
                LANGUAGE plpgsql AS $$
                DECLARE
                  j jsonb := old_json::jsonb;
                  surcharge bigint := COALESCE((j->>'PcSpecialBandSurchargeToman')::bigint, 20000);
                  step bigint := (j->>'StepToman')::bigint;
                  dir text := j->>'Direction';
                  out_lines jsonb := '[]'::jsonb;
                  elem jsonb;
                  day_rate bigint;
                  gid text;
                BEGIN
                  IF COALESCE((j->>'SchemaVersion')::int, 0) = 2 THEN
                    RETURN old_json;
                  END IF;
                  FOR elem IN SELECT value FROM jsonb_array_elements(COALESCE(j->'Lines', '[]'::jsonb))
                  LOOP
                    IF elem->>'GroupId' NOT IN ('pc-normal', 'pc-vip') THEN
                      out_lines := out_lines || jsonb_build_array(elem);
                    END IF;
                  END LOOP;
                  FOREACH gid IN ARRAY ARRAY['pc-normal', 'pc-vip']
                  LOOP
                    day_rate := NULL;
                    SELECT (e->>'HourlyToman')::bigint INTO day_rate
                    FROM jsonb_array_elements(COALESCE(j->'Lines', '[]'::jsonb)) e
                    WHERE e->>'GroupId' = gid
                      AND e->>'PlayMode' = 'Offline'
                      AND (e->>'PlayerCount')::int = 1
                      AND (e->>'IsSpecialBand')::boolean = false
                    LIMIT 1;
                    IF day_rate IS NULL THEN
                      SELECT (e->>'HourlyToman')::bigint INTO day_rate
                      FROM jsonb_array_elements(COALESCE(j->'Lines', '[]'::jsonb)) e
                      WHERE e->>'GroupId' = gid
                        AND e->>'PlayMode' = 'Online'
                        AND e->>'PlayerCount' IS NULL
                        AND (e->>'IsSpecialBand')::boolean = false
                      LIMIT 1;
                    END IF;
                    IF day_rate IS NULL THEN
                      CONTINUE;
                    END IF;
                    out_lines := out_lines || jsonb_build_array(
                      jsonb_build_object('GroupId', gid, 'PlayMode', 'Online', 'PlayerCount', null, 'IsSpecialBand', false, 'HourlyToman', day_rate),
                      jsonb_build_object('GroupId', gid, 'PlayMode', 'Online', 'PlayerCount', null, 'IsSpecialBand', true, 'HourlyToman', day_rate + surcharge));
                    IF EXISTS (
                      SELECT 1 FROM jsonb_array_elements(COALESCE(j->'Lines', '[]'::jsonb)) e
                      WHERE e->>'GroupId' = gid AND e->>'PlayMode' = 'Offline' AND (e->>'PlayerCount')::int = 1 AND (e->>'IsSpecialBand')::boolean = false
                    ) THEN
                      out_lines := out_lines || jsonb_build_array((
                        SELECT e FROM jsonb_array_elements(COALESCE(j->'Lines', '[]'::jsonb)) e
                        WHERE e->>'GroupId' = gid AND e->>'PlayMode' = 'Offline' AND (e->>'PlayerCount')::int = 1 AND (e->>'IsSpecialBand')::boolean = false LIMIT 1));
                    END IF;
                    IF EXISTS (
                      SELECT 1 FROM jsonb_array_elements(COALESCE(j->'Lines', '[]'::jsonb)) e
                      WHERE e->>'GroupId' = gid AND e->>'PlayMode' = 'Offline' AND (e->>'PlayerCount')::int = 1 AND (e->>'IsSpecialBand')::boolean = true
                    ) THEN
                      out_lines := out_lines || jsonb_build_array((
                        SELECT e FROM jsonb_array_elements(COALESCE(j->'Lines', '[]'::jsonb)) e
                        WHERE e->>'GroupId' = gid AND e->>'PlayMode' = 'Offline' AND (e->>'PlayerCount')::int = 1 AND (e->>'IsSpecialBand')::boolean = true LIMIT 1));
                    ELSE
                      out_lines := out_lines || jsonb_build_array(
                        jsonb_build_object('GroupId', gid, 'PlayMode', 'Offline', 'PlayerCount', 1, 'IsSpecialBand', true, 'HourlyToman', day_rate + surcharge));
                    END IF;
                  END LOOP;
                  RETURN jsonb_build_object('SchemaVersion', 2, 'StepToman', step, 'Direction', dir, 'Lines', out_lines)::text;
                END;
                $$;

                UPDATE rate_card_snapshot rcs
                SET "Json" = rewrite_pc_open_snapshot_v2(rcs."Json")
                FROM sessions s
                INNER JOIN stations st ON s."StationId" = st."Id"
                INNER JOIN station_groups g ON st."GroupId" = g."Id"
                WHERE rcs."Id" = s."RateCardSnapshotId"
                  AND g."Kind" = 'Pc'
                  AND s."Status" <> 'Closed';

                DROP FUNCTION rewrite_pc_open_snapshot_v2(text);
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
            migrationBuilder.Sql(
                """
                UPDATE stations s
                SET "DefaultPlayMode" = 'Offline'
                FROM station_groups g
                WHERE s."GroupId" = g."Id" AND g."Kind" = 'Pc';

                UPDATE sessions sess
                SET "Mode" = 'Offline', "PlayerCount" = 1
                FROM stations st
                INNER JOIN station_groups g ON st."GroupId" = g."Id"
                WHERE sess."StationId" = st."Id"
                  AND g."Kind" = 'Pc'
                  AND sess."Status" <> 'Closed';

                UPDATE play_segments seg
                SET "Mode" = 'Offline', "PlayerCount" = 1
                FROM sessions sess
                INNER JOIN stations st ON sess."StationId" = st."Id"
                INNER JOIN station_groups g ON st."GroupId" = g."Id"
                WHERE seg."SessionId" = sess."Id"
                  AND g."Kind" = 'Pc'
                  AND sess."Status" <> 'Closed';
                """);

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
                UPDATE branch_rounding
                SET "PcSpecialBandSurchargeToman" = COALESCE((
                    SELECT MAX(night."HourlyToman" - day."HourlyToman")
                    FROM rate_lines day
                    INNER JOIN rate_lines night
                      ON night."GroupId" = day."GroupId"
                     AND night."PlayMode" = 'Online'
                     AND night."IsSpecialBand" = true
                    WHERE day."GroupId" IN ('pc-normal', 'pc-vip')
                      AND day."PlayMode" = 'Online'
                      AND day."PlayerCount" IS NULL
                      AND day."IsSpecialBand" = false
                ), 20000);

                CREATE OR REPLACE FUNCTION revert_pc_open_snapshot_v1(v2_json text, surcharge bigint) RETURNS text
                LANGUAGE plpgsql AS $$
                DECLARE
                  j jsonb := v2_json::jsonb;
                  step bigint := (j->>'StepToman')::bigint;
                  dir text := j->>'Direction';
                  out_lines jsonb := '[]'::jsonb;
                  elem jsonb;
                  gid text;
                  day_rate bigint;
                BEGIN
                  IF COALESCE((j->>'SchemaVersion')::int, 0) <> 2 THEN
                    RETURN v2_json;
                  END IF;
                  FOR elem IN SELECT value FROM jsonb_array_elements(COALESCE(j->'Lines', '[]'::jsonb))
                  LOOP
                    gid := elem->>'GroupId';
                    IF gid IN ('pc-normal', 'pc-vip')
                       AND elem->>'PlayMode' = 'Online'
                       AND (elem->>'IsSpecialBand')::boolean = true THEN
                      CONTINUE;
                    END IF;
                    IF gid IN ('pc-normal', 'pc-vip')
                       AND elem->>'PlayMode' = 'Online'
                       AND (elem->>'IsSpecialBand')::boolean = false THEN
                      out_lines := out_lines || jsonb_build_array(
                        jsonb_build_object(
                          'GroupId', gid,
                          'PlayMode', 'Offline',
                          'PlayerCount', 1,
                          'IsSpecialBand', false,
                          'HourlyToman', (elem->>'HourlyToman')::bigint));
                      CONTINUE;
                    END IF;
                    out_lines := out_lines || jsonb_build_array(elem);
                  END LOOP;
                  FOREACH gid IN ARRAY ARRAY['pc-normal', 'pc-vip']
                  LOOP
                    IF NOT EXISTS (
                      SELECT 1 FROM jsonb_array_elements(out_lines) e
                      WHERE e->>'GroupId' = gid AND e->>'PlayMode' = 'Offline' AND (e->>'IsSpecialBand')::boolean = true
                    ) THEN
                      SELECT (e->>'HourlyToman')::bigint INTO day_rate
                      FROM jsonb_array_elements(out_lines) e
                      WHERE e->>'GroupId' = gid
                        AND e->>'PlayMode' = 'Offline'
                        AND (e->>'PlayerCount')::int = 1
                        AND (e->>'IsSpecialBand')::boolean = false
                      LIMIT 1;
                      IF day_rate IS NOT NULL THEN
                        out_lines := out_lines || jsonb_build_array(
                          jsonb_build_object('GroupId', gid, 'PlayMode', 'Offline', 'PlayerCount', 1, 'IsSpecialBand', true, 'HourlyToman', day_rate + surcharge));
                      END IF;
                    END IF;
                  END LOOP;
                  RETURN jsonb_build_object(
                    'StepToman', step,
                    'Direction', dir,
                    'PcSpecialBandSurchargeToman', surcharge,
                    'Lines', out_lines)::text;
                END;
                $$;

                UPDATE rate_card_snapshot rcs
                SET "Json" = revert_pc_open_snapshot_v1(rcs."Json", br."PcSpecialBandSurchargeToman")
                FROM sessions s
                INNER JOIN stations st ON s."StationId" = st."Id"
                INNER JOIN station_groups g ON st."GroupId" = g."Id"
                CROSS JOIN branch_rounding br
                WHERE rcs."Id" = s."RateCardSnapshotId"
                  AND g."Kind" = 'Pc'
                  AND s."Status" <> 'Closed';

                DROP FUNCTION revert_pc_open_snapshot_v1(text, bigint);

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