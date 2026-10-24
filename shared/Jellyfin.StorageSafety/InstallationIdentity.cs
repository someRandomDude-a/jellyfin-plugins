using Microsoft.Data.Sqlite;

namespace Jellyfin.PluginSupport;

/// <summary>Plugin-owned identity only. Never touches Jellyfin's native database.</summary>
public sealed class InstallationIdentity(string directory, string database, string keys)
{
    private string Marker => Path.Combine(directory, "installation.id");
    public void BeforeOpen()
    {
        if (!File.Exists(database) && (File.Exists(Marker) || Directory.Exists(keys) && Directory.EnumerateFileSystemEntries(keys).Any()))
            throw new InvalidDataException("Plugin database is missing. Restore the database and original installation artifacts together.");
    }
    public void Verify(SqliteConnection db)
    {
        if (!File.Exists(Marker)) return; // Legacy installation: enroll only after existing integrity/key checks pass.
        var expected = File.ReadAllText(Marker);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='plugin_installation_identity'";
        if (cmd.ExecuteScalar() is null) throw new InvalidDataException("Plugin database does not match installation identity.");
        cmd.CommandText = "SELECT identity FROM plugin_installation_identity WHERE id=1";
        if (!Guid.TryParseExact(expected, "N", out _) || cmd.ExecuteScalar() is not string actual || actual != expected)
            throw new InvalidDataException("Plugin database does not match installation identity.");
    }
    public void Seal(SqliteConnection db)
    {
        Verify(db);
        using var tx = db.BeginTransaction(); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS plugin_installation_identity(id INTEGER PRIMARY KEY CHECK(id=1), identity TEXT NOT NULL); SELECT identity FROM plugin_installation_identity WHERE id=1";
        var identity = cmd.ExecuteScalar() as string;
        if (identity is null)
        {
            identity = Guid.NewGuid().ToString("N");
            cmd.CommandText = "INSERT INTO plugin_installation_identity VALUES(1,$identity)";
            cmd.Parameters.AddWithValue("$identity", identity); cmd.ExecuteNonQuery();
        }
        if (!Guid.TryParseExact(identity, "N", out _)) throw new InvalidDataException("Invalid plugin installation identity.");
        tx.Commit();
        if (!File.Exists(Marker))
        {
            // A crash after the database commit can resume from the same persisted identity.
            // CreateNew prevents overwriting unfamiliar installation artifacts.
            using var file = new FileStream(Marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var bytes = System.Text.Encoding.ASCII.GetBytes(identity); file.Write(bytes); file.Flush(flushToDisk: true);
        }
        Verify(db);
    }
}
