using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.PrivateSyncPlay;

// Own database only. Isolated assembly: never opens Jellyfin's database or persists parties/credentials.
public sealed class HubOutbox(string path)
{
    private readonly object _gate = new();
    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 1, Pooling = false }.ToString());
        try
        {
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version is not (0 or 1 or 2)) { db.Dispose(); throw new InvalidDataException("Unsupported integration store version."); }
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS events (
              id TEXT PRIMARY KEY, event_id TEXT NOT NULL, kind TEXT NOT NULL, payload TEXT NOT NULL,
              created INTEGER NOT NULL, expires INTEGER NOT NULL, next_attempt INTEGER NOT NULL,
              attempts INTEGER NOT NULL DEFAULT 0, status TEXT NOT NULL DEFAULT 'pending', lease INTEGER NOT NULL DEFAULT 0, terminal INTEGER NOT NULL DEFAULT 0, last_error TEXT NOT NULL DEFAULT '');
            """;
        command.ExecuteNonQuery();
        using var migration = db.BeginTransaction();command.Transaction = migration;
        command.CommandText = version == 1 ? "ALTER TABLE events ADD COLUMN last_error TEXT NOT NULL DEFAULT ''; PRAGMA user_version=2;" : "PRAGMA user_version=2;";
        command.ExecuteNonQuery();migration.Commit();return db;
        }
        catch { db.Dispose();throw; }
    }
    public void Enqueue(Guid eventId, string kind, string payload, DateTimeOffset now, TimeSpan freshness)
    {
        if (kind is not ("notification" or "audit") || System.Text.Encoding.UTF8.GetByteCount(payload) > 60000) throw new InvalidDataException("Invalid event summary.");
        lock (_gate)
        {
            using var db = Open();using var tx = db.BeginTransaction();using var command = db.CreateCommand();command.Transaction = tx;
            command.CommandText = "SELECT COUNT(*), COALESCE(SUM(length(payload)),0) FROM events";
            using (var reader = command.ExecuteReader())
            { reader.Read();if (reader.GetInt64(0) >= 10000 || reader.GetInt64(1) >= 32 * 1024 * 1024) throw new IOException("Integration outbox is full."); }
            command.CommandText = "INSERT OR IGNORE INTO events(id,event_id,kind,payload,created,expires,next_attempt) VALUES($id,$event,$kind,$payload,$now,$expires,$now)";
            command.Parameters.AddWithValue("$id", eventId + ":" + kind);command.Parameters.AddWithValue("$event", eventId.ToString());
            command.Parameters.AddWithValue("$kind", kind);command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());command.Parameters.AddWithValue("$expires", (now + freshness).ToUnixTimeSeconds());
            command.ExecuteNonQuery();tx.Commit();
        }
    }
    public HubPending[] Claim(DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open();using var tx = db.BeginTransaction();using var command = db.CreateCommand();command.Transaction = tx;
            command.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());command.Parameters.AddWithValue("$prune", now.AddDays(-30).ToUnixTimeSeconds());
            command.CommandText = "DELETE FROM events WHERE terminal>0 AND terminal<$prune; UPDATE events SET status=CASE kind WHEN 'notification' THEN 'local_fallback' ELSE 'dead_letter' END,terminal=$now WHERE status IN ('pending','delivering') AND expires<=$now";
            command.ExecuteNonQuery();
            command.CommandText = "SELECT id,kind,payload,attempts,created FROM events WHERE next_attempt<=$now AND (status='pending' OR (status='delivering' AND lease<=$now)) ORDER BY created,id LIMIT 8";
            var rows = new List<HubPending>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4))));
            command.CommandText = "UPDATE events SET status='delivering',lease=$lease WHERE id=$id";
            command.Parameters.AddWithValue("$lease", now.AddSeconds(30).ToUnixTimeSeconds());var id = command.Parameters.Add("$id", SqliteType.Text);
            foreach (var row in rows) { id.Value = row.Id;command.ExecuteNonQuery(); }
            tx.Commit();return rows.ToArray();
        }
    }
    public void Complete(HubPending row, string result, DateTimeOffset now)
    {
        lock (_gate)
        {
            using var db = Open();using var command = db.CreateCommand();
            var attempts = row.Attempts + 1;
            var status = result is "accepted" or "duplicate" ? "delivered" : result == "rejected" && row.Kind == "audit" ? "dead_letter" : result is "rejected" or "local_fallback" ? "local_fallback"
                : row.Kind == "notification" && attempts >= 5 ? "local_fallback" : "pending";
            command.CommandText = "UPDATE events SET status=$status, attempts=$attempts, next_attempt=$next,lease=0,terminal=$terminal,last_error=$error WHERE id=$id";
            command.Parameters.AddWithValue("$status", status);command.Parameters.AddWithValue("$attempts", attempts);command.Parameters.AddWithValue("$id", row.Id);
            command.Parameters.AddWithValue("$terminal", status == "pending" ? 0 : now.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$error", result is "accepted" or "duplicate" ? "" : result is "rejected" or "local_fallback" ? result : "unavailable");
            command.Parameters.AddWithValue("$next", now.AddSeconds(Math.Min(3600, 5 * Math.Pow(2, Math.Min(attempts, 10)))).ToUnixTimeSeconds());command.ExecuteNonQuery();
        }
    }
    public Dictionary<string, long> Counts()
    {
        lock (_gate) { using var db = Open();using var cmd = db.CreateCommand();cmd.CommandText = "SELECT status,COUNT(*) FROM events GROUP BY status";using var r = cmd.ExecuteReader();var result = new Dictionary<string, long>();while (r.Read()) result[r.GetString(0)] = r.GetInt64(1);return result; }
    }
}
public sealed record HubPending(string Id, string Kind, string Payload, int Attempts, DateTimeOffset Created);
