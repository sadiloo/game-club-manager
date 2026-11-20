using Npgsql;

namespace GameClub.Server.Tests;

/// <summary>Unique prefixed Postgres database for PR52 R4 middleware / LastSeen tests.</summary>
internal static class Pr52R4HttpLastSeenTestDatabase
{
    private static readonly object Gate = new();
    private static string? _connectionString;

    public static string ConnectionString
    {
        get
        {
            lock (Gate)
            {
                return _connectionString ?? throw new InvalidOperationException("PR52 R4 test database was not initialized.");
            }
        }
    }

    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_connectionString is not null)
            {
                return;
            }

            var dbName = "gameclub_test_pr52r4_" + Guid.NewGuid().ToString("N")[..12];
            var admin = Environment.GetEnvironmentVariable("GAMECLUB_DATABASE_ADMIN")
                ?? "Host=localhost;Database=postgres;Username=gameclub;Password=gameclub";
            using (var conn = new NpgsqlConnection(admin))
            {
                conn.Open();
                using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", conn);
                exists.Parameters.AddWithValue("name", dbName);
                var present = exists.ExecuteScalar() is not null;
                if (!present)
                {
                    using var create = new NpgsqlCommand($"""CREATE DATABASE "{dbName}" OWNER gameclub""", conn);
                    create.ExecuteNonQuery();
                }
            }

            _connectionString =
                $"Host=localhost;Database={dbName};Username=gameclub;Password=gameclub";
            Environment.SetEnvironmentVariable("GAMECLUB_TEST_DATABASE", _connectionString);
            Environment.SetEnvironmentVariable("GAMECLUB_DATABASE", _connectionString);
        }
    }
}

public sealed class Pr52R4HttpLastSeenWebApplicationFactory : GameClubWebApplicationFactory
{
    static Pr52R4HttpLastSeenWebApplicationFactory()
    {
        Pr52R4HttpLastSeenTestDatabase.EnsureInitialized();
    }
}

[CollectionDefinition("pr52-r4-http-lastseen", DisableParallelization = true)]
public sealed class Pr52R4HttpLastSeenCollection : ICollectionFixture<Pr52R4HttpLastSeenWebApplicationFactory>;