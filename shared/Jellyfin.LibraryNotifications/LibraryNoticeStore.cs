using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jellyfin.LibraryNotifications;

/// <summary>Bounded durable item intake, shared-source schema inside the owning plugin DB. No paths or credentials.</summary>
public sealed class LibraryNoticeStore
{
    private readonly string path;
    private readonly object gate = new();
    public LibraryNoticeStore(string databasePath)
    {
        path = databasePath; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS library_notice_schema(id INTEGER PRIMARY KEY CHECK(id=1),version INTEGER NOT NULL); INSERT OR IGNORE INTO library_notice_schema VALUES(1,1);"; command.ExecuteNonQuery();
        command.CommandText = "SELECT version FROM library_notice_schema WHERE id=1";
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidDataException("Unsupported library notice schema.");
        command.CommandText = "CREATE TABLE IF NOT EXISTS library_notice_settings(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL,enabled INTEGER NOT NULL,episodes INTEGER NOT NULL,movies INTEGER NOT NULL,since INTEGER); INSERT OR IGNORE INTO library_notice_settings VALUES(1,0,0,1,1,NULL); CREATE TABLE IF NOT EXISTS library_notices(id TEXT PRIMARY KEY,item TEXT NOT NULL UNIQUE,kind TEXT NOT NULL,created INTEGER NOT NULL,notice TEXT,inbox_done INTEGER NOT NULL DEFAULT 0,discord_state INTEGER NOT NULL DEFAULT 0,attempts INTEGER NOT NULL DEFAULT 0,next INTEGER NOT NULL); CREATE INDEX IF NOT EXISTS library_notices_due ON library_notices(next); UPDATE library_notices SET discord_state=3 WHERE discord_state=2;";
        command.ExecuteNonQuery(); // Interrupted direct sends become uncertain, never automatic replay.
        command.CommandText = "CREATE TABLE IF NOT EXISTS library_notice_scope(id INTEGER PRIMARY KEY CHECK(id=1),json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS library_notice_origins(id TEXT PRIMARY KEY,legacy_inbox INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS library_notice_imports(source TEXT PRIMARY KEY);"; command.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 10,
            Mode = File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "installation.id")) ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA synchronous=FULL"; command.ExecuteNonQuery(); return db;
    }
    public LibraryNoticeSettings Settings()
    {
        lock (gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT revision,enabled,episodes,movies,since,(SELECT json FROM library_notice_scope WHERE id=1) FROM library_notice_settings WHERE id=1"; using var reader = cmd.ExecuteReader(); reader.Read(); return new(reader.GetInt64(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4))) { LibraryIds = reader.IsDBNull(5) ? [] : JsonSerializer.Deserialize<Guid[]>(reader.GetString(5))! }; }
    }
    public void SaveSettings(LibraryNoticeSettings settings)
    {
        if (settings.LibraryIds is null || settings.LibraryIds.Length > 100 || settings.LibraryIds.Contains(Guid.Empty)) throw new ArgumentException("Invalid library selection.");
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(deferred:false); using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "UPDATE library_notice_settings SET revision=revision+1,since=CASE WHEN $enabled=1 AND enabled=0 THEN $now WHEN $enabled=0 THEN NULL ELSE since END,enabled=$enabled,episodes=$episodes,movies=$movies WHERE id=1 AND revision=$revision";
            cmd.Parameters.AddWithValue("$enabled", settings.Enabled); cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds()); cmd.Parameters.AddWithValue("$episodes", settings.Episodes); cmd.Parameters.AddWithValue("$movies", settings.Movies); cmd.Parameters.AddWithValue("$revision", settings.Revision);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("revision_changed");
            cmd.Parameters.Clear(); cmd.CommandText = "INSERT INTO library_notice_scope VALUES(1,$scope) ON CONFLICT(id) DO UPDATE SET json=excluded.json"; cmd.Parameters.AddWithValue("$scope",JsonSerializer.Serialize(settings.LibraryIds)); cmd.ExecuteNonQuery(); tx.Commit();
        }
    }
    public bool Capture(Guid itemId, string kind, DateTimeOffset now, LibraryNotice? prepared = null)
    {
        if (itemId == Guid.Empty || kind is not ("episode" or "movie")) return false;
        if (prepared is not null)
        {
            LibraryNoticeFactory.Validate(prepared);
            if (prepared.ItemId != itemId || prepared.Kind != kind) throw new ArgumentException("Mismatched library notice.");
        }
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(deferred: false); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT count(*) FROM library_notices WHERE item=$item"; cmd.Parameters.AddWithValue("$item", itemId.ToString("D"));
            if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) { tx.Commit(); return true; }
            cmd.CommandText = "DELETE FROM library_notices WHERE created<$cutoff AND inbox_done=1 AND discord_state IN (1,3); SELECT count(*) FROM library_notices";
            cmd.Parameters.AddWithValue("$cutoff", now.AddDays(-90).ToUnixTimeSeconds());
            if (Convert.ToInt64(cmd.ExecuteScalar()) >= 10000) return false;
            cmd.CommandText = "INSERT OR IGNORE INTO library_notices(id,item,kind,created,notice,next) VALUES($id,$item,$kind,$created,$notice,$next)";
            cmd.Parameters.AddWithValue("$id", (prepared?.Id ?? Guid.NewGuid()).ToString("D")); cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds()); cmd.Parameters.AddWithValue("$notice", prepared is null ? DBNull.Value : JsonSerializer.Serialize(prepared, LibraryNoticeFactory.Json)); cmd.Parameters.AddWithValue("$next", now.AddSeconds(prepared is null ? 30 : 0).ToUnixTimeSeconds());
            cmd.ExecuteNonQuery(); tx.Commit(); return true; // A duplicate is an acknowledged no-op.
        }
    }
    public LibraryJob[] Due(DateTimeOffset now)
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,item,kind,created,notice,inbox_done,discord_state,attempts,COALESCE((SELECT legacy_inbox FROM library_notice_origins o WHERE o.id=library_notices.id),0) FROM library_notices WHERE next<=$now AND attempts<10 AND (inbox_done=0 OR discord_state=0) ORDER BY created LIMIT 20"; cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            using var reader = cmd.ExecuteReader(); var jobs = new List<LibraryJob>();
            while (reader.Read()) jobs.Add(new(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetString(2), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)), reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<LibraryNotice>(reader.GetString(4), LibraryNoticeFactory.Json), reader.GetBoolean(5), reader.GetInt32(6), reader.GetInt32(7),reader.GetBoolean(8)));
            return jobs.ToArray();
        }
    }
    public void Prepare(Guid id, LibraryNotice notice) { LibraryNoticeFactory.Validate(notice); if (id != notice.Id) throw new ArgumentException("Mismatched library job."); Update(id, "notice=COALESCE(notice,$value)", JsonSerializer.Serialize(notice, LibraryNoticeFactory.Json)); }
    public void InboxDone(Guid id) => Update(id, "inbox_done=1");
    public void DiscordClaim(Guid id) => Update(id, "discord_state=2");
    public void DiscordDone(Guid id) => Update(id, "discord_state=1");
    public void DiscordUncertain(Guid id) => Update(id, "discord_state=3");
    public void Defer(Guid id, DateTimeOffset now) => Update(id, "attempts=attempts+1,next=$value", now.AddMinutes(1).ToUnixTimeSeconds());
    private void Update(Guid id, string assignments, object? value = null)
    {
        lock (gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE library_notices SET " + assignments + " WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString("D")); if (value is not null) cmd.Parameters.AddWithValue("$value", value); cmd.ExecuteNonQuery(); }
    }
    public object Health()
    {
        lock (gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT count(*),coalesce(sum(CASE WHEN inbox_done=0 OR discord_state=0 THEN 1 ELSE 0 END),0),coalesce(sum(CASE WHEN discord_state=3 THEN 1 ELSE 0 END),0),coalesce(sum(CASE WHEN attempts>=10 THEN 1 ELSE 0 END),0) FROM library_notices"; using var reader = cmd.ExecuteReader(); reader.Read(); return new { retained = reader.GetInt64(0), pending = reader.GetInt64(1), uncertainDiscord = reader.GetInt64(2), failed = reader.GetInt64(3) }; }
    }
    // Read-only legacy sources; preserve IDs/terminal and uncertain delivery states.
    // Coordinated packages remove their old observers before this owner starts.
    public void Import(string source, bool legacyInbox)
    {
        if (!File.Exists(source) || Path.GetFullPath(source) == Path.GetFullPath(path)) return;
        lock (gate) {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText="SELECT count(*) FROM library_notice_imports WHERE source=$source"; cmd.Parameters.AddWithValue("$source",source);
            if(Convert.ToInt32(cmd.ExecuteScalar())!=0)return;
            using var old = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=source,Mode=SqliteOpenMode.ReadOnly,Pooling=false }.ToString()); old.Open(); using var read=old.CreateCommand();
            using var snapshot=old.BeginTransaction(deferred:true); read.Transaction=snapshot;
            read.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND name='library_notice_schema'"; if(Convert.ToInt32(read.ExecuteScalar())==0)return;
            read.CommandText="SELECT version FROM library_notice_schema WHERE id=1"; if(Convert.ToInt32(read.ExecuteScalar())!=1)throw new InvalidDataException("Unknown legacy library notification schema.");
            using var tx=db.BeginTransaction(deferred:false); cmd.Transaction=tx;
            if(legacyInbox){read.CommandText="SELECT enabled,episodes,movies,since FROM library_notice_settings WHERE id=1";using var settings=read.ExecuteReader();if(settings.Read()){cmd.CommandText="UPDATE library_notice_settings SET enabled=$e,episodes=$p,movies=$m,since=$s WHERE id=1 AND revision=0";cmd.Parameters.AddWithValue("$e",settings.GetInt32(0));cmd.Parameters.AddWithValue("$p",settings.GetInt32(1));cmd.Parameters.AddWithValue("$m",settings.GetInt32(2));cmd.Parameters.AddWithValue("$s",settings.GetValue(3));cmd.ExecuteNonQuery();}}
            read.CommandText="SELECT id,item,kind,created,notice,inbox_done,discord_state,attempts,next FROM library_notices ORDER BY created";
            using(var rows=read.ExecuteReader())while(rows.Read()){
                cmd.Parameters.Clear();cmd.CommandText="INSERT INTO library_notices VALUES($id,$item,$kind,$created,$notice,$inbox,$discord,$attempts,$next) ON CONFLICT(item) DO UPDATE SET inbox_done="+(legacyInbox?"MAX(inbox_done,excluded.inbox_done)":"inbox_done")+",discord_state=MAX(discord_state,excluded.discord_state)";
                var notice=rows.IsDBNull(4)?null:JsonSerializer.Deserialize<LibraryNotice>(rows.GetString(4),LibraryNoticeFactory.Json);if(notice is not null&&legacyInbox)notice=notice with{LegacySource="account-hub"};
                foreach(var pair in new Dictionary<string,object?>{["$id"]=rows.GetString(0),["$item"]=rows.GetString(1),["$kind"]=rows.GetString(2),["$created"]=rows.GetInt64(3),["$notice"]=notice is null?null:JsonSerializer.Serialize(notice,LibraryNoticeFactory.Json),["$inbox"]=rows.GetInt32(5),["$discord"]=rows.GetInt32(6)==2?3:rows.GetInt32(6),["$attempts"]=rows.GetInt32(7),["$next"]=rows.GetInt64(8)})cmd.Parameters.AddWithValue(pair.Key,pair.Value??DBNull.Value);
                cmd.ExecuteNonQuery();
                cmd.Parameters.Clear();cmd.CommandText="SELECT count(*) FROM library_notices";
                if(Convert.ToInt64(cmd.ExecuteScalar())>10000)throw new InvalidDataException("Legacy library intake exceeds capacity; original sources retained for administrator recovery.");
                if(legacyInbox){cmd.CommandText="INSERT OR IGNORE INTO library_notice_origins SELECT id,1 FROM library_notices WHERE id=$id";cmd.Parameters.AddWithValue("$id",rows.GetString(0));cmd.ExecuteNonQuery();}
            }
            cmd.Parameters.Clear();cmd.CommandText="INSERT INTO library_notice_imports VALUES($source)";cmd.Parameters.AddWithValue("$source",source);cmd.ExecuteNonQuery();snapshot.Commit();tx.Commit();
        }
    }
}
