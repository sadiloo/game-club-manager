using Microsoft.Data.Sqlite;

namespace GameClub.Kiosk.Core;

public enum OfflineEventKind
{
    Login = 0,
    Heartbeat = 1,
    Logout = 2,
    Lockout = 3,
    GameLaunch = 4,
}

public sealed record OfflineEvent(
    long Id,
    string SessionId,
    OfflineEventKind Kind,
    DateTimeOffset At,
    string Payload,
    bool Synced);

public sealed class LocalStore : IDisposable
{
    private static int _sqliteReady;
    private readonly SqliteConnection _connection;

    public LocalStore(string path)
    {
        EnsureSqlite();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA busy_timeout=5000;");
        Execute("""
            CREATE TABLE IF NOT EXISTS events (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              session_id TEXT NOT NULL,
              kind TEXT NOT NULL,
              at_utc TEXT NOT NULL,
              payload TEXT NOT NULL,
              synced INTEGER NOT NULL DEFAULT 0
            );
            """);
        Execute("""
            CREATE TABLE IF NOT EXISTS catalog_cache (
              id INTEGER PRIMARY KEY CHECK (id = 1),
              json TEXT NOT NULL,
              cached_at_utc TEXT NOT NULL
            );
            """);
        Execute("""
            CREATE TABLE IF NOT EXISTS open_session (
              id INTEGER PRIMARY KEY CHECK (id = 1),
              json TEXT NOT NULL
            );
            """);
        Execute("""
            CREATE TABLE IF NOT EXISTS stop_list (
              name TEXT PRIMARY KEY
            );
            """);
        Execute("""
            CREATE TABLE IF NOT EXISTS pending_reports (
              session_id TEXT PRIMARY KEY,
              json TEXT NOT NULL,
              attempts INTEGER NOT NULL DEFAULT 0,
              next_attempt_utc TEXT NOT NULL
            );
            """);
    }

    public void Append(string sessionId, OfflineEventKind kind, DateTimeOffset at, string payload)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO events (session_id, kind, at_utc, payload, synced)
            VALUES ($session, $kind, $at, $payload, 0);
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$at", TehranStamp.Format(at));
        command.Parameters.AddWithValue("$payload", payload ?? "");
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<OfflineEvent> ReadSession(string sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, kind, at_utc, payload, synced
            FROM events
            WHERE session_id = $session
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        return ReadEvents(command);
    }

    public IReadOnlyList<OfflineEvent> Unsynced()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, session_id, kind, at_utc, payload, synced
            FROM events
            WHERE synced = 0
            ORDER BY id;
            """;
        return ReadEvents(command);
    }

    public void MarkSynced(IEnumerable<long> ids)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var id in ids)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE events SET synced = 1 WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void MarkSessionSynced(string sessionId)
    {
        using var transaction = _connection.BeginTransaction();
        using (var update = _connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE events SET synced = 1 WHERE session_id = $session AND synced = 0;";
            update.Parameters.AddWithValue("$session", sessionId);
            update.ExecuteNonQuery();
        }

        using (var prune = _connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM events WHERE session_id = $session AND kind = $kind AND synced = 1;";
            prune.Parameters.AddWithValue("$session", sessionId);
            prune.Parameters.AddWithValue("$kind", OfflineEventKind.Heartbeat.ToString());
            prune.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void WriteCheckpoint(string sessionId, DateTimeOffset at, long playedSeconds)
    {
        using var transaction = _connection.BeginTransaction();
        using (var delete = _connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM events WHERE session_id = $session AND kind = $kind;";
            delete.Parameters.AddWithValue("$session", sessionId);
            delete.Parameters.AddWithValue("$kind", OfflineEventKind.Heartbeat.ToString());
            delete.ExecuteNonQuery();
        }

        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO events (session_id, kind, at_utc, payload, synced)
                VALUES ($session, $kind, $at, $payload, 0);
                """;
            insert.Parameters.AddWithValue("$session", sessionId);
            insert.Parameters.AddWithValue("$kind", OfflineEventKind.Heartbeat.ToString());
            insert.Parameters.AddWithValue("$at", TehranStamp.Format(at));
            insert.Parameters.AddWithValue("$payload", playedSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void SavePending(PendingReport report)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pending_reports (session_id, json, attempts, next_attempt_utc)
            VALUES ($session, $json, $attempts, $next)
            ON CONFLICT(session_id) DO UPDATE SET
              json = excluded.json,
              attempts = excluded.attempts,
              next_attempt_utc = excluded.next_attempt_utc;
            """;
        command.Parameters.AddWithValue("$session", report.SessionId);
        command.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(report));
        command.Parameters.AddWithValue("$attempts", report.Attempts);
        command.Parameters.AddWithValue("$next", report.NextAttemptAt ?? "");
        command.ExecuteNonQuery();
    }

    public PendingReport? PeekPending()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT json FROM pending_reports ORDER BY rowid LIMIT 1;";
        if (command.ExecuteScalar() is not string json || json.Length == 0)
        {
            return null;
        }

        return System.Text.Json.JsonSerializer.Deserialize<PendingReport>(json);
    }

    public bool HasPending()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pending_reports;";
        var count = (long)(command.ExecuteScalar() ?? 0L);
        return count > 0;
    }

    public void DeletePending(string sessionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM pending_reports WHERE session_id = $session;";
        command.Parameters.AddWithValue("$session", sessionId);
        command.ExecuteNonQuery();
    }

    public void SaveCatalog(IReadOnlyList<GameEntry> games, DateTimeOffset at)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO catalog_cache (id, json, cached_at_utc)
            VALUES (1, $json, $at)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json, cached_at_utc = excluded.cached_at_utc;
            """;
        command.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(games));
        command.Parameters.AddWithValue("$at", TehranStamp.Format(at));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<GameEntry> ReadCatalog()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT json FROM catalog_cache WHERE id = 1;";
        if (command.ExecuteScalar() is not string json || json.Length == 0)
        {
            return [];
        }

        return System.Text.Json.JsonSerializer.Deserialize<List<GameEntry>>(json) ?? [];
    }

    public void SaveOpenSession(OpenSessionSnapshot snapshot)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO open_session (id, json)
            VALUES (1, $json)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json;
            """;
        command.Parameters.AddWithValue("$json", AccountCodec.WriteSession(snapshot));
        command.ExecuteNonQuery();
    }

    public OpenSessionSnapshot? ReadOpenSession()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT json FROM open_session WHERE id = 1;";
        return command.ExecuteScalar() is string json && json.Length > 0
            ? AccountCodec.ReadSession(json)
            : null;
    }

    public void ClearOpenSession()
    {
        Execute("DELETE FROM open_session;");
    }

    public void SaveStopList(IEnumerable<string> names)
    {
        using var transaction = _connection.BeginTransaction();
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM stop_list;";
            clear.ExecuteNonQuery();
        }

        foreach (var name in names)
        {
            var normalized = ProcessPolicy.Normalize(name);
            if (normalized.Length == 0)
            {
                continue;
            }

            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO stop_list (name) VALUES ($name);";
            insert.Parameters.AddWithValue("$name", normalized);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<string> ReadStopList()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT name FROM stop_list ORDER BY name;";
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    public void ClearStopList() => Execute("DELETE FROM stop_list;");

    public void Dispose() => _connection.Dispose();

    private static void EnsureSqlite()
    {
        if (Interlocked.Exchange(ref _sqliteReady, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static List<OfflineEvent> ReadEvents(SqliteCommand command)
    {
        var events = new List<OfflineEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var kind = (OfflineEventKind)Enum.Parse(typeof(OfflineEventKind), reader.GetString(2));
            events.Add(new OfflineEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                kind,
                DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetString(4),
                reader.GetInt64(5) != 0));
        }

        return events;
    }
}
