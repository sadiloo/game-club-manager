using Npgsql;

namespace GameClub.Server.Tests;

/// <summary>Unique prefixed Postgres database for kiosk attach seam tests (R6).</summary>
internal sealed class KioskAttachPathSeamTestDatabase : IDisposable
{
    private readonly object _gate = new();
    private string? _databaseName;
    private NpgsqlConnectionStringBuilder? _adminBuilder;
    private bool _initialized;

    public string ConnectionString { get; private set; } = "";

    public void EnsureInitialized()
    {
        lock (_gate)
        {
            if (_initialized)
            {
                return;
            }

            var template = new NpgsqlConnectionStringBuilder(ResolveTemplateConnectionString());
            _databaseName = "gameclub_test_kiosk_attach_" + Guid.NewGuid().ToString("N")[..12];
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

    private static string ResolveTemplateConnectionString() =>
        Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
        ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")
        ?? "Host=localhost;Database=gameclub_test;Username=gameclub;Password=gameclub";

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
                $"kiosk-attach-seam: failed to drop database \"{_databaseName}\": {ex.GetType().Name}: {ex.Message}");
        }
    }
}

public sealed class KioskAttachPathSeamWebApplicationFactory : GameClubWebApplicationFactory
{
    private readonly KioskAttachPathSeamTestDatabase _database = new();

    public KioskAttachPathSeamWebApplicationFactory()
    {
        _database.EnsureInitialized();
    }

    protected override string? DedicatedConnectionString => _database.ConnectionString;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _database.Dispose();
        }

        base.Dispose(disposing);
    }
}