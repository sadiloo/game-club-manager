using Npgsql;

namespace GameClub.Server.Tests;

/// <summary>Creates and drops uniquely named Postgres databases for server tests (R8a L-1).</summary>
internal class GameClubPrefixedTestDatabase : IDisposable
{
    public const string NamePrefix = "gameclub_test_";

    private readonly object _gate = new();
    private string? _databaseName;
    private NpgsqlConnectionStringBuilder? _adminBuilder;
    private bool _initialized;

    public GameClubPrefixedTestDatabase(string nameStem = "")
    {
        NameStem = string.IsNullOrEmpty(nameStem) ? NamePrefix : nameStem;
        if (!NameStem.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Database name stem must start with {NamePrefix}.", nameof(nameStem));
        }
    }

    public string NameStem { get; }

    public string DatabaseName => _databaseName ?? throw new InvalidOperationException("Database not initialized.");

    public string ConnectionString { get; private set; } = "";

    public void EnsureInitialized()
    {
        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            var template = ResolveServerTemplate();
            _databaseName = NameStem + Guid.NewGuid().ToString("N")[..12];
            TestDatabaseGuard.EnsureDeletableDatabaseName(_databaseName);

            _adminBuilder = new NpgsqlConnectionStringBuilder(template.ConnectionString)
            {
                Database = "postgres",
            };

            using (var conn = new NpgsqlConnection(_adminBuilder.ConnectionString))
            {
                conn.Open();
                using var create = new NpgsqlCommand($"""CREATE DATABASE "{_databaseName}" """, conn);
                create.ExecuteNonQuery();
            }

            template.Database = _databaseName;
            ConnectionString = template.ConnectionString;
            _initialized = true;
        }
    }

    public void Dispose()
    {
        if (_databaseName is null || _adminBuilder is null)
        {
            return;
        }

        try
        {
            using var conn = new NpgsqlConnection(_adminBuilder.ConnectionString);
            conn.Open();
            using var drop = new NpgsqlCommand(
                $"""DROP DATABASE IF EXISTS "{_databaseName}" WITH (FORCE)""",
                conn);
            drop.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"test-db: failed to drop database \"{_databaseName}\": {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static NpgsqlConnectionStringBuilder ResolveServerTemplate()
    {
        foreach (var envName in new[] { "GAMECLUB_TEST_DATABASE", "GAMECLUB_DATABASE" })
        {
            var raw = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var builder = new NpgsqlConnectionStringBuilder(raw);
            if (!string.IsNullOrWhiteSpace(builder.Database)
                && builder.Database.StartsWith(NamePrefix, StringComparison.Ordinal))
            {
                return builder;
            }

            return new NpgsqlConnectionStringBuilder(raw)
            {
                Database = "postgres",
            };
        }

        return new NpgsqlConnectionStringBuilder(
            "Host=localhost;Database=postgres;Username=gameclub;Password=gameclub");
    }

    public static string ResolveDedicatedConnectionStringOrCreate(GameClubPrefixedTestDatabase database)
    {
        foreach (var envName in new[] { "GAMECLUB_TEST_DATABASE", "GAMECLUB_DATABASE" })
        {
            var raw = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var builder = new NpgsqlConnectionStringBuilder(raw);
            var name = builder.Database;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (string.Equals(name, "gameclub_test", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Refusing shared database gameclub_test. Use a " + NamePrefix + "<unique> name in "
                    + envName + ".");
            }

            TestDatabaseGuard.EnsureDeletableDatabaseName(name);
            return builder.ConnectionString;
        }

        database.EnsureInitialized();
        return database.ConnectionString;
    }
}

internal static class TestDatabaseGuard
{
    public static void EnsureDeletableDatabaseName(string? databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException("Database name is missing.");
        }

        if (string.Equals(databaseName, "gameclub_test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete the shared database gameclub_test.");
        }

        if (!databaseName.StartsWith(GameClubPrefixedTestDatabase.NamePrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Refusing to delete database \"" + databaseName
                + "\". Test databases must be named " + GameClubPrefixedTestDatabase.NamePrefix + "<unique>.");
        }
    }
}