using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace GameClub.Kiosk.Core.Catalog;

public sealed class SqliteCatalogCache(string dbPath)
{
    private readonly string _connection = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;

    public async Task SaveAsync(IEnumerable<GameCatalogItem> items, long catalogVersion, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
#if NET10_0_OR_GREATER
        await using var conn = new SqliteConnection(_connection);
#else
        using var conn = new SqliteConnection(_connection);
#endif
        await conn.OpenAsync(ct);
#if NET10_0_OR_GREATER
        await using var tx = await conn.BeginTransactionAsync(ct);
#else
        using var tx = conn.BeginTransaction();
#endif
#if NET10_0_OR_GREATER
        await using var wipe = conn.CreateCommand();
#else
        using var wipe = conn.CreateCommand();
#endif
#if NET10_0_OR_GREATER
        wipe.Transaction = (SqliteTransaction)tx;
#else
        wipe.Transaction = tx;
#endif
        wipe.CommandText = "DELETE FROM catalog_entries; DELETE FROM catalog_meta;";
        await wipe.ExecuteNonQueryAsync(ct);
        foreach (var item in items)
        {
#if NET10_0_OR_GREATER
            await using var ins = conn.CreateCommand();
#else
            using var ins = conn.CreateCommand();
#endif
#if NET10_0_OR_GREATER
            ins.Transaction = (SqliteTransaction)tx;
#else
            ins.Transaction = tx;
#endif
            ins.CommandText = """
                INSERT INTO catalog_entries (id, json)
                VALUES ($id, $json);
                """;
            ins.Parameters.AddWithValue("$id", item.Id);
            ins.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item));
            await ins.ExecuteNonQueryAsync(ct);
        }

#if NET10_0_OR_GREATER
        await using var meta = conn.CreateCommand();
#else
        using var meta = conn.CreateCommand();
#endif
#if NET10_0_OR_GREATER
        meta.Transaction = (SqliteTransaction)tx;
#else
        meta.Transaction = tx;
#endif
        meta.CommandText = "INSERT INTO catalog_meta (key, value) VALUES ('version', $v);";
        meta.Parameters.AddWithValue("$v", catalogVersion.ToString());
        await meta.ExecuteNonQueryAsync(ct);
#if NET10_0_OR_GREATER
        await tx.CommitAsync(ct);
#else
        tx.Commit();
#endif
    }

    public async Task<IReadOnlyList<GameCatalogItem>> LoadAsync(CancellationToken ct = default)
    {
        await EnsureAsync(ct);
#if NET10_0_OR_GREATER
        await using var conn = new SqliteConnection(_connection);
#else
        using var conn = new SqliteConnection(_connection);
#endif
        await conn.OpenAsync(ct);
#if NET10_0_OR_GREATER
        await using var cmd = conn.CreateCommand();
#else
        using var cmd = conn.CreateCommand();
#endif
        cmd.CommandText = "SELECT json FROM catalog_entries ORDER BY id;";
        var list = new List<GameCatalogItem>();
#if NET10_0_OR_GREATER
        await using var reader = await cmd.ExecuteReaderAsync(ct);
#else
        using var reader = await cmd.ExecuteReaderAsync(ct);
#endif
        while (await reader.ReadAsync(ct))
        {
            var json = reader.GetString(0);
            var item = JsonSerializer.Deserialize<GameCatalogItem>(json);
            if (item is not null)
            {
                list.Add(item);
            }
        }

        return list;
    }

    public async Task<long> LastVersionAsync(CancellationToken ct = default)
    {
        await EnsureAsync(ct);
#if NET10_0_OR_GREATER
        await using var conn = new SqliteConnection(_connection);
#else
        using var conn = new SqliteConnection(_connection);
#endif
        await conn.OpenAsync(ct);
#if NET10_0_OR_GREATER
        await using var cmd = conn.CreateCommand();
#else
        using var cmd = conn.CreateCommand();
#endif
        cmd.CommandText = "SELECT value FROM catalog_meta WHERE key = 'version' LIMIT 1;";
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is string s && long.TryParse(s, out var v) ? v : 0;
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
#if NET10_0_OR_GREATER
        await using var conn = new SqliteConnection(_connection);
#else
        using var conn = new SqliteConnection(_connection);
#endif
        await conn.OpenAsync(ct);
#if NET10_0_OR_GREATER
        await using var cmd = conn.CreateCommand();
#else
        using var cmd = conn.CreateCommand();
#endif
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS catalog_entries (
              id INTEGER PRIMARY KEY,
              json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS catalog_meta (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
