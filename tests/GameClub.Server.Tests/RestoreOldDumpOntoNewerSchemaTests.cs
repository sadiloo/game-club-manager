using System.Diagnostics;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace GameClub.Server.Tests;

/// <summary>
/// R10h BL-rr6-2: recovery path 2 restores a dump taken by the OLDER version onto a database the newer version already
/// migrated. installer/tools/restore.ps1 (the real script, real pg_dump / pg_restore / psql) must recreate the schema
/// after its verified safety dump, so the restored database carries only the old schema and history, and a later
/// upgrade (Migrate to latest) succeeds without 42P07. The negative control replays the pre-R10h
/// `pg_restore --clean --if-exists` and shows the 42P07 this test exists for.
/// </summary>
public sealed class RestoreOldDumpOntoNewerSchemaTests
{
    // The schema of release 0.0.9: the newer migration after it creates kiosk_credentials.
    private const string OldMigration = "20261118175351_PcInternetRates";
    private const string NewerOnlyTable = "kiosk_credentials";
    private const string MarkerUser = "r10h_restore_marker";

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static AppDbContext Db(string cs)
        => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options);

    private static (int Exit, string Output) Run(string file, IEnumerable<string> args, IDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(300_000)) { p.Kill(true); throw new TimeoutException($"{file} timed out"); }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private static async Task<object?> ScalarAsync(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private sealed record Prepared(string Dump, string WorkDir, NpgsqlConnectionStringBuilder Conn, Dictionary<string, string> PgEnv);

    /// <summary>Old schema + marker row, dump it (the "pre-upgrade" dump), migrate to latest, drop the marker.</summary>
    private static async Task<Prepared> PrepareAsync(GameClubPrefixedTestDatabase isolated)
    {
        isolated.EnsureInitialized();
        var cs = isolated.ConnectionString;
        await using (var db = Db(cs))
        {
            await db.GetService<IMigrator>().MigrateAsync(OldMigration);
        }
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var ins = new NpgsqlCommand(
                "INSERT INTO operator_users (\"Id\", \"Username\", \"PinHash\", \"Role\", \"IsActive\") VALUES (@id, @u, 'x', 1, true)", conn);
            ins.Parameters.AddWithValue("id", Guid.NewGuid());
            ins.Parameters.AddWithValue("u", MarkerUser);
            await ins.ExecuteNonQueryAsync();
        }

        var b = new NpgsqlConnectionStringBuilder(cs);
        var work = Path.Combine(Path.GetTempPath(), "gcm-r10h-restore-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(work);
        var pgEnv = new Dictionary<string, string> { ["PGPASSWORD"] = b.Password ?? "" };
        var dump = Path.Combine(work, "pre-upgrade-0.0.9-old.dump");
        var d = Run("pg_dump", new[] { "-Fc", "-h", b.Host!, "-p", b.Port.ToString(), "-U", b.Username!, "-d", b.Database!, "-f", dump }, pgEnv);
        Assert.True(d.Exit == 0, "pg_dump of the old schema failed: " + d.Output);

        await using (var db = Db(cs))
        {
            await db.Database.MigrateAsync();
        }
        Assert.Equal(1L, await ScalarAsync(cs, $"SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name='{NewerOnlyTable}'"));
        await ScalarAsync(cs, $"DELETE FROM operator_users WHERE \"Username\"='{MarkerUser}'");
        return new Prepared(dump, work, b, pgEnv);
    }

    private static async Task AssertMigrateToLatestSucceedsAsync(string cs)
    {
        await using var db = Db(cs);
        await db.Database.MigrateAsync();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var all = db.Database.GetMigrations().ToList();
        Assert.Equal(all, applied);
    }

    [Fact]
    public async Task restore_ps1_of_an_old_version_dump_onto_a_newer_schema_then_upgrade_succeeds_without_42P07()
    {
        using var isolated = new GameClubPrefixedTestDatabase(GameClubPrefixedTestDatabase.NamePrefix + "restoreold_");
        var prep = await PrepareAsync(isolated);
        try
        {
            var programData = Path.Combine(prep.WorkDir, "ProgramData");
            Directory.CreateDirectory(Path.Combine(programData, "Checkpoint", "backups"));
            var serverJson = Path.Combine(prep.WorkDir, "server.json");
            var conn = $"Host={prep.Conn.Host};Port={prep.Conn.Port};Database={prep.Conn.Database};Username={prep.Conn.Username};Password={prep.Conn.Password}";
            File.WriteAllText(serverJson, System.Text.Json.JsonSerializer.Serialize(new { ConnectionStrings = new { Default = conn } }));
            var restore = Path.Combine(RepoRoot(), "installer", "tools", "restore.ps1");
            // Service cmdlets are Windows-only; the box/ubuntu run stubs them like BackupRestoreConfig.Tests does.
            // Off Windows, CheckpointDbConfig.ps1's "C:\Program Files\Checkpoint\PostgreSQL" probe needs a C: drive
            // (an empty one, so the real pg_dump / pg_restore / psql on PATH are used), as in Invoke-BoxStaticTest.sh.
            var cRoot = Path.Combine(prep.WorkDir, "c");
            Directory.CreateDirectory(cRoot);
            var drive = OperatingSystem.IsWindows() ? "" : $"$null = New-PSDrive -Name C -PSProvider FileSystem -Root '{cRoot.Replace("'", "''")}' -Scope Global ; ";
            var command = drive + "function Stop-Service { param($Name, $ErrorAction) } ; function Start-Service { param($Name, $ErrorAction) } ; "
                + $"& '{restore.Replace("'", "''")}' -DumpPath '{prep.Dump.Replace("'", "''")}' -ServerJsonPath '{serverJson.Replace("'", "''")}' ; exit $LASTEXITCODE";
            var env = new Dictionary<string, string> { ["ProgramData"] = programData };
            var r = Run("pwsh", new[] { "-NoProfile", "-NonInteractive", "-Command", command }, env);
            Assert.True(r.Exit == 0, "restore.ps1 failed: " + r.Output);

            var log = File.ReadAllText(Path.Combine(programData, "Checkpoint", "logs", "restore.log"));
            Assert.Matches(@"CHECKPOINT_RESTORE_SCHEMA_RECREATE database=\S+ safety_dump=\S+pre-restore-\d{8}-\d{6}(-\d+)?\.dump input_dump=\S+ action=drop_and_create_public_schema", log);
            Assert.Contains("CHECKPOINT_RESTORE_DONE", log);
            var safety = Directory.GetFiles(Path.Combine(programData, "Checkpoint", "backups"), "pre-restore-*.dump");
            Assert.Single(safety);
            Assert.True(new FileInfo(safety[0]).Length > 0);

            var cs = isolated.ConnectionString;
            Assert.Equal(0L, await ScalarAsync(cs, $"SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name='{NewerOnlyTable}'"));
            Assert.Equal(OldMigration, await ScalarAsync(cs, "SELECT max(\"MigrationId\") FROM \"__EFMigrationsHistory\""));
            Assert.Equal(1L, await ScalarAsync(cs, $"SELECT count(*) FROM operator_users WHERE \"Username\"='{MarkerUser}'"));
            // The recreated schema keeps the PostgreSQL 15+ default owner.
            Assert.Equal("pg_database_owner", await ScalarAsync(cs, "SELECT nspowner::regrole::text FROM pg_namespace WHERE nspname='public'"));

            await AssertMigrateToLatestSucceedsAsync(cs);
        }
        finally
        {
            Directory.Delete(prep.WorkDir, recursive: true);
        }
    }

    [Fact]
    public async Task negative_control_pre_R10h_pg_restore_clean_leaves_newer_tables_and_upgrade_fails_42P07()
    {
        using var isolated = new GameClubPrefixedTestDatabase(GameClubPrefixedTestDatabase.NamePrefix + "restoreold_");
        var prep = await PrepareAsync(isolated);
        try
        {
            var b = prep.Conn;
            var r = Run("pg_restore", new[] { "--clean", "--if-exists", "-h", b.Host!, "-p", b.Port.ToString(), "-U", b.Username!, "-d", b.Database!, prep.Dump }, prep.PgEnv);
            Assert.True(r.Exit == 0, "pg_restore --clean failed: " + r.Output);
            var cs = isolated.ConnectionString;
            Assert.Equal(1L, await ScalarAsync(cs, $"SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name='{NewerOnlyTable}'"));
            var ex = await Assert.ThrowsAsync<PostgresException>(() => AssertMigrateToLatestSucceedsAsync(cs));
            Assert.Equal("42P07", ex.SqlState);
        }
        finally
        {
            Directory.Delete(prep.WorkDir, recursive: true);
        }
    }
}
