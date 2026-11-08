#if NET10_0_OR_GREATER
using Microsoft.Data.Sqlite;

namespace GameClub.Kiosk.Core.Offline;

public interface IOfflineEventQueue
{
    Task EnqueueAsync(Guid eventId, string type, string payloadJson, DateTime createdUtc, CancellationToken ct = default);
    Task<IReadOnlyList<QueuedEvent>> PendingAsync(CancellationToken ct = default);
    Task AckAsync(Guid eventId, CancellationToken ct = default);
}

public sealed record QueuedEvent(Guid EventId, string Type, string PayloadJson, DateTime CreatedUtc);

public sealed class SqliteOfflineEventQueue(string dbPath) : IOfflineEventQueue
{
    private readonly string _connection = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;

    public async Task EnqueueAsync(Guid eventId, string type, string payloadJson, DateTime createdUtc, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var conn = new SqliteConnection(_connection);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO outbound_events (event_id, type, payload_json, created_utc)
            VALUES ($id, $type, $payload, $created);
            """;
        cmd.Parameters.AddWithValue("$id", eventId.ToString());
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$payload", payloadJson);
        cmd.Parameters.AddWithValue("$created", createdUtc.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<QueuedEvent>> PendingAsync(CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var conn = new SqliteConnection(_connection);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, type, payload_json, created_utc
            FROM outbound_events WHERE acked_utc IS NULL ORDER BY created_utc;
            """;
        var list = new List<QueuedEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new QueuedEvent(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return list;
    }

    public async Task AckAsync(Guid eventId, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        await using var conn = new SqliteConnection(_connection);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE outbound_events SET acked_utc = $ack WHERE event_id = $id;";
        cmd.Parameters.AddWithValue("$ack", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", eventId.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connection);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS outbound_events (
              event_id TEXT PRIMARY KEY,
              type TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              created_utc TEXT NOT NULL,
              acked_utc TEXT NULL);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
#endif
