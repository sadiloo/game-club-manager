using System.Text.RegularExpressions;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GameClub.Server.Tests;

/// <summary>
/// R10g item 2 (rr5 L-rr5-4, BL-rr5-1): every SQL string in installer/tools/acceptance/*.ps1 is checked against a
/// freshly migrated database before Windows CI ever runs it. SELECT/UPDATE/INSERT/DELETE go through EXPLAIN (planned,
/// never executed; wrong tables or columns fail), CREATE/DROP DATABASE run for real on a throw-away gameclub_test_ name.
/// PowerShell variables are bound to harmless literals. The old "AmountToman" fingerprint query is the negative control.
/// </summary>
public sealed class AcceptanceHarnessSqlSchemaTests
{
    // Pinned so that an extraction regression (statements silently not found) fails too.
    private const int ExpectedStatementCount = 12;

    private static readonly Regex SqlStart = new(@"^\s*(SELECT|UPDATE|INSERT|DELETE|CREATE DATABASE|DROP DATABASE)\b", RegexOptions.Compiled);

    // A psql result tag such as "UPDATE 1" compared in PowerShell is not SQL; every harness statement ends with ';'.
    private static bool IsSql(string body) => SqlStart.IsMatch(body) && body.TrimEnd().EndsWith(';');

    internal sealed record HarnessSql(string File, int Line, string Sql);

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>Here-strings, double-quoted and single-quoted PowerShell strings whose text starts with a SQL verb.</summary>
    internal static List<HarnessSql> Extract(string file, string text)
    {
        var found = new List<(int Index, string Sql)>();
        var consumed = new List<(int Start, int End)>();
        foreach (Match m in Regex.Matches(text, "@\"\\r?\\n(.*?)\\r?\\n\"@", RegexOptions.Singleline))
        {
            consumed.Add((m.Index, m.Index + m.Length));
            if (IsSql(m.Groups[1].Value)) found.Add((m.Index, m.Groups[1].Value));
        }

        bool Inside(int i) => consumed.Any(c => i >= c.Start && i < c.End);
        // "..." with "" and `" escapes; '...' with '' escapes; one line each.
        foreach (Match m in Regex.Matches(text, "\"((?:[^\"`\\r\\n]|\"\"|`.)*)\""))
        {
            if (Inside(m.Index)) continue;
            var body = m.Groups[1].Value.Replace("\"\"", "\"").Replace("`\"", "\"");
            if (IsSql(body)) found.Add((m.Index, body));
        }
        foreach (Match m in Regex.Matches(text, "'((?:[^'\\r\\n]|'')*)'"))
        {
            if (Inside(m.Index)) continue;
            var body = m.Groups[1].Value.Replace("''", "'");
            if (IsSql(body)) found.Add((m.Index, body));
        }

        return found
            .GroupBy(f => f.Index).Select(g => g.First())
            .OrderBy(f => f.Index)
            .Select(f => new HarnessSql(file, text[..f.Index].Count(c => c == '\n') + 1, f.Sql))
            .ToList();
    }

    /// <summary>Bind PowerShell variables to literals that type-check in the statement's context.</summary>
    internal static string Bind(string sql, string databaseName)
    {
        sql = Regex.Replace(sql, @"\$\([^)]*\)", "r10g");
        sql = Regex.Replace(sql, "\"\\$(dbName|DatabaseName)\"", "\"" + databaseName + "\"");
        sql = Regex.Replace(sql, @"'\$id'", "'00000000-0000-0000-0000-000000000001'");
        sql = Regex.Replace(sql, @"'\$\w+'", "'r10g'");
        sql = Regex.Replace(sql, @"\$\w+", "1");
        return sql;
    }

    internal static IEnumerable<string> Statements(string sql)
        => sql.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0);

    /// <summary>Returns the error messages (empty = all statements valid against the migrated schema).</summary>
    internal static async Task<List<string>> CheckAsync(IEnumerable<HarnessSql> items, string connectionString)
    {
        var errors = new List<string>();
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" }.ConnectionString;
        var scratchDb = GameClubPrefixedTestDatabase.NamePrefix + "sqlchk_" + Guid.NewGuid().ToString("N")[..12];
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var item in items)
        {
            foreach (var stmt in Statements(Bind(item.Sql, scratchDb)))
            {
                try
                {
                    if (stmt.StartsWith("CREATE DATABASE", StringComparison.Ordinal) || stmt.StartsWith("DROP DATABASE", StringComparison.Ordinal))
                    {
                        Assert.Contains(scratchDb, stmt);
                        await using var a = new NpgsqlConnection(admin);
                        await a.OpenAsync();
                        await using var c = new NpgsqlCommand(stmt, a);
                        await c.ExecuteNonQueryAsync();
                    }
                    else
                    {
                        await using var tx = await conn.BeginTransactionAsync();
                        await using var c = new NpgsqlCommand("EXPLAIN " + stmt, conn, tx);
                        await c.ExecuteNonQueryAsync();
                        await tx.RollbackAsync();
                    }
                }
                catch (PostgresException ex)
                {
                    errors.Add($"{item.File}:{item.Line}: {ex.MessageText} :: {stmt}");
                }
            }
        }

        await using (var a = new NpgsqlConnection(admin))
        {
            await a.OpenAsync();
            await using var c = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{scratchDb}\" WITH (FORCE)", a);
            await c.ExecuteNonQueryAsync();
        }

        return errors;
    }

    private static async Task<string> MigratedDatabaseAsync(GameClubPrefixedTestDatabase isolated)
    {
        isolated.EnsureInitialized();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(isolated.ConnectionString).Options;
        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();
        return isolated.ConnectionString;
    }

    internal static List<HarnessSql> ExtractAll()
    {
        var dir = Path.Combine(RepoRoot(), "installer", "tools", "acceptance");
        Assert.True(Directory.Exists(dir), dir);
        return Directory.GetFiles(dir, "*.ps1").OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(f => Extract(Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();
    }

    [Fact]
    public async Task every_acceptance_harness_sql_statement_is_valid_against_the_migrated_schema()
    {
        var items = ExtractAll();
        var listing = string.Join("\n", items.Select(i => $"{i.File}:{i.Line}: {i.Sql.ReplaceLineEndings(" ")}"));
        Assert.True(items.Count == ExpectedStatementCount,
            $"expected {ExpectedStatementCount} SQL strings in installer/tools/acceptance, found {items.Count}:\n{listing}");
        foreach (var table in new[] { "wallet_ledger", "kiosk_lease_signing_keys", "kiosk_devices", "operator_users", "rate_lines", "__EFMigrationsHistory", "customers", "sessions", "invoices" })
        {
            Assert.Contains(items, i => i.Sql.Contains(table, StringComparison.Ordinal));
        }

        using var isolated = new GameClubPrefixedTestDatabase(GameClubPrefixedTestDatabase.NamePrefix + "harnesssql_");
        var cs = await MigratedDatabaseAsync(isolated);
        var errors = await CheckAsync(items, cs);
        Assert.True(errors.Count == 0, "acceptance harness SQL does not match the schema:\n" + string.Join("\n", errors));
    }

    [Fact]
    public async Task negative_control_old_AmountToman_fingerprint_query_is_refused()
    {
        const string oldCommon = "    $q = @\"\nSELECT\n coalesce((SELECT sum(\"AmountToman\") FROM wallet_ledger), 0),\n (SELECT count(*) FROM kiosk_lease_signing_keys),\n coalesce((SELECT left(\"LeaseVerifyPublicKeyPem\", 32) FROM kiosk_devices LIMIT 1), '');\n\"@\n";
        var items = Extract("old-Invoke-AcceptanceCommon.ps1", oldCommon);
        Assert.Single(items);
        using var isolated = new GameClubPrefixedTestDatabase(GameClubPrefixedTestDatabase.NamePrefix + "harnesssql_");
        var cs = await MigratedDatabaseAsync(isolated);
        var errors = await CheckAsync(items, cs);
        Assert.Contains(errors, e => e.Contains("column \"AmountToman\" does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void b2_upgrade_compares_real_and_gift_wallet_ledger_sums_separately()
    {
        // BL-rr5-1: the fingerprint sums the two real money columns, and B-2 compares each one before/after upgrade.
        var dir = Path.Combine(RepoRoot(), "installer", "tools", "acceptance");
        var common = File.ReadAllText(Path.Combine(dir, "Invoke-AcceptanceCommon.ps1"));
        var b2 = File.ReadAllText(Path.Combine(dir, "Invoke-B2-Upgrade.ps1"));
        Assert.Contains("sum(\"RealDeltaToman\") FROM wallet_ledger", common);
        Assert.Contains("sum(\"GiftDeltaToman\") FROM wallet_ledger", common);
        Assert.Contains("WalletLedgerRealSumToman = [int64]$parts[0]", common);
        Assert.Contains("WalletLedgerGiftSumToman = [int64]$parts[1]", common);
        Assert.Contains("if ($billingBefore.WalletLedgerRealSumToman -ne $billingAfter.WalletLedgerRealSumToman) {", b2);
        Assert.Contains("if ($billingBefore.WalletLedgerGiftSumToman -ne $billingAfter.WalletLedgerGiftSumToman) {", b2);
        Assert.DoesNotContain("sum(\"AmountToman\")", common + b2);
    }

    [Fact]
    public void extractor_binds_powershell_variables_and_unescapes_quotes()
    {
        var items = Extract("x.ps1", "$x = & $psql -c \"UPDATE operator_users SET \"\"PinHash\"\"='$h' WHERE \"\"Username\"\"='$u';\"\n$y = 'SELECT count(*) || '':'' FROM t;'\nif ($o -ne \"UPDATE 1\") { }\n$z = \"CREATE DATABASE `\"$dbName`\";\"");
        Assert.Equal(3, items.Count);
        Assert.Equal("UPDATE operator_users SET \"PinHash\"='r10g' WHERE \"Username\"='r10g';", Bind(items[0].Sql, "d"));
        Assert.Equal("SELECT count(*) || ':' FROM t;", Bind(items[1].Sql, "d"));
        Assert.Equal("CREATE DATABASE \"gameclub_test_q\";", Bind(items[2].Sql, "gameclub_test_q"));
    }
}
