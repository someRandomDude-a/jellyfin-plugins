using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AccountHub.Core.Seerr;

/// <summary>Independent named schema in the Hub DB; never touches EF migrations or PRAGMA user_version.</summary>
public sealed class SeerrStore(HubPaths paths) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<T> UpdateAsync<T>(Func<SeerrState, T> action, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false, DefaultTimeout = 10,
                Mode = File.Exists(Path.Combine(paths.DataDirectory, "installation.id")) ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate }.ToString());
            await db.OpenAsync(ct);
            using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS hub_seerr_schema (id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL, state TEXT NOT NULL); INSERT OR IGNORE INTO hub_seerr_schema VALUES(1,1,'{}');";
            await cmd.ExecuteNonQueryAsync(ct);
            cmd.CommandText = "SELECT version FROM hub_seerr_schema WHERE id=1";
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) != 1) throw new HubException("seerr_schema_incompatible", "Restore a compatible Seerr module", true);
            cmd.CommandText = "SELECT state FROM hub_seerr_schema WHERE id=1";
            var state = JsonSerializer.Deserialize<SeerrState>((string)(await cmd.ExecuteScalarAsync(ct))!, Json) ?? throw new InvalidDataException();
            var result = action(state);
            cmd.CommandText = "UPDATE hub_seerr_schema SET state=$state WHERE id=1";
            cmd.Parameters.AddWithValue("$state", JsonSerializer.Serialize(state, Json));
            await cmd.ExecuteNonQueryAsync(ct); tx.Commit(); return result;
        }
        finally { gate.Release(); }
    }
    public Task<SeerrState> ReadAsync(CancellationToken ct = default) => UpdateAsync(s => s, ct);
    public void Dispose() => gate.Dispose();
}
