using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Jellyfin.Plugin.DiscordBridge;

public enum InteractionClaim { Claimed, Duplicate, CapacityReached }

/// <summary>Own secrets and interaction receipts only. Never persist links, session tokens or party invitations.</summary>
public sealed class BridgeStore
{
    private readonly string path;
    private readonly IDataProtector protector;
    private readonly object gate = new();
    public BridgeStore(string directory)
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "discord-bridge.db");
        var keys = Path.Combine(directory, "keys");
        var installation = new Jellyfin.PluginSupport.InstallationIdentity(directory, path, keys);
        installation.BeforeOpen();
        using var db = Open();
        installation.Verify(db);
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(command.ExecuteScalar());
        if (version is not (0 or 1 or 2)) throw new InvalidDataException("Unsupported Discord Bridge schema.");
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS settings(id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL, json TEXT NOT NULL, token TEXT); CREATE TABLE IF NOT EXISTS interactions(id TEXT PRIMARY KEY, created INTEGER NOT NULL, status TEXT NOT NULL); CREATE TABLE IF NOT EXISTS configuration_audit(id INTEGER PRIMARY KEY AUTOINCREMENT,actor TEXT NOT NULL,revision INTEGER NOT NULL,method TEXT NOT NULL,fallback TEXT,created INTEGER NOT NULL); PRAGMA user_version=2;";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE TABLE IF NOT EXISTS delivery_receipts(id TEXT PRIMARY KEY,fingerprint TEXT NOT NULL,status TEXT NOT NULL,created INTEGER NOT NULL)";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT token FROM settings WHERE id=1";
        if (command.ExecuteScalar() is string && (!Directory.Exists(keys) || !Directory.EnumerateFiles(keys, "key-*.xml").Any()))
            throw new InvalidDataException("Restore Discord Bridge database and original keys together.");
        protector = DataProtectionProvider.Create(new DirectoryInfo(keys), b => b.SetApplicationName("Jellyfin.DiscordBridge")).CreateProtector("settings.v1");
        _ = Read(); // Wrong/missing keys fail closed, never replace unreadable credentials.
        installation.Seal(db);
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5,
            Mode = File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "installation.id")) ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate }.ToString());
        try {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA synchronous=FULL"; cmd.ExecuteNonQuery(); return db;
        } catch { db.Dispose(); throw; }
    }
    public (Configuration Settings, long Revision, string? Token) Read()
    {
        lock (gate)
        {
            using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT revision,json,token FROM settings WHERE id=1";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return (new Configuration(), 0, null);
            var settings = JsonSerializer.Deserialize<Configuration>(reader.GetString(1))!;
            foreach (var retired in new[] { "party.activity", "party.activity.control", "party.devices" }) {
                settings.Permissions.Defaults.Remove(retired); foreach (var rules in settings.Permissions.Users.Values) rules.Remove(retired);
            }
            return (settings, reader.GetInt64(0), reader.IsDBNull(2) ? null : protector.Unprotect(reader.GetString(2)));
        }
    }
    public void Save(Configuration value, long revision, string? token, bool clear, Guid? actor = null, string method = "native_confirmation", string? fallback = null)
    {
        Validate(value);
        if (clear && token is not null) throw new ArgumentException("Clear and replacement are mutually exclusive.");
        if (token is not null && (token.Length is < 20 or > 512 || token.Any(char.IsWhiteSpace))) throw new ArgumentException("Invalid bot token.");
        lock (gate)
        {
            var old = Read();
            if (old.Revision != revision) throw new InvalidOperationException("revision_changed");
            value.LibraryEnabledSince = value.LibraryAnnouncementsEnabled ? old.Settings.LibraryAnnouncementsEnabled ? old.Settings.LibraryEnabledSince : DateTimeOffset.UtcNow : null;
            using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO settings VALUES(1,$revision,$json,$token) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,json=excluded.json,token=excluded.token";
            cmd.Parameters.AddWithValue("$revision", revision + 1); cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value));
            var next = clear ? null : token ?? old.Token;
            cmd.Parameters.AddWithValue("$token", next is null ? DBNull.Value : protector.Protect(next)); cmd.ExecuteNonQuery();
            if (actor is { } user)
            {
                cmd.Parameters.Clear();
                cmd.CommandText = "INSERT INTO configuration_audit(actor,revision,method,fallback,created) VALUES($actor,$revision,$method,$fallback,$created); DELETE FROM configuration_audit WHERE created<$cutoff";
                cmd.Parameters.AddWithValue("$actor", user.ToString("D")); cmd.Parameters.AddWithValue("$revision", revision + 1);
                cmd.Parameters.AddWithValue("$method", method); cmd.Parameters.AddWithValue("$fallback", fallback is null ? DBNull.Value : fallback);
                cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds()); cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
    public bool Claim(string id) => TryClaim(id) == InteractionClaim.Claimed;
    public InteractionClaim TryClaim(string id)
    {
        if (!Snowflake(id)) throw new ArgumentException("Invalid interaction ID.");
        lock (gate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(deferred: false); using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT COUNT(*) FROM interactions WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);
            if(Convert.ToInt64(cmd.ExecuteScalar())!=0)return InteractionClaim.Duplicate;
            cmd.CommandText = "DELETE FROM interactions WHERE created<$cutoff; SELECT COUNT(*) FROM interactions";
            cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds());
            if (Convert.ToInt32(cmd.ExecuteScalar()) >= 10000) return InteractionClaim.CapacityReached;
            cmd.CommandText = "INSERT OR IGNORE INTO interactions VALUES($id,$now,'claimed')";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
            // Query this statement's effect explicitly. Do not infer admission
            // from a provider's aggregate ExecuteNonQuery count.
            cmd.CommandText="SELECT changes()";var inserted=Convert.ToInt64(cmd.ExecuteScalar())==1;
            tx.Commit(); return inserted?InteractionClaim.Claimed:InteractionClaim.Duplicate;
        }
    }
    public void CompleteInteraction(string id,bool completed){lock(gate){using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE interactions SET status=$status WHERE id=$id AND status='claimed'";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$status",completed?"completed":"uncertain");cmd.ExecuteNonQuery();}}
    public static bool Snowflake(string? value) => value is { Length: > 0 and <= 20 } && ulong.TryParse(value, out var id) && id != 0;
    // Fingerprints only: never store message text, recovery links or invitation tokens.
    public string BeginDelivery(Guid id,string fingerprint,bool claimAllowed=true){
        lock(gate){using var db=Open();using var tx=db.BeginTransaction(deferred:false);using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="SELECT fingerprint,status FROM delivery_receipts WHERE id=$id";cmd.Parameters.AddWithValue("$id",id.ToString("D"));
            using(var reader=cmd.ExecuteReader()){if(reader.Read()){if(reader.GetString(0)!=fingerprint)throw new ArgumentException("Delivery ID already belongs to another message");return reader.GetString(1);}}
            if(!claimAllowed)return "unavailable";
            cmd.CommandText="SELECT COUNT(*) FROM delivery_receipts";if(Convert.ToInt64(cmd.ExecuteScalar())>=100000)throw new InvalidOperationException("Delivery receipt capacity reached");
            cmd.CommandText="INSERT INTO delivery_receipts VALUES($id,$fingerprint,'claimed',$now)";cmd.Parameters.AddWithValue("$fingerprint",fingerprint);cmd.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToUnixTimeSeconds());cmd.ExecuteNonQuery();tx.Commit();return "new";
        }
    }
    public void CompleteDelivery(Guid id,bool delivered){lock(gate){using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="UPDATE delivery_receipts SET status=$status WHERE id=$id AND status='claimed'";cmd.Parameters.AddWithValue("$status",delivered?"delivered":"uncertain");cmd.Parameters.AddWithValue("$id",id.ToString("D"));cmd.ExecuteNonQuery();}}
    public static void Validate(Configuration value)
    {
        BridgePermissions.Validate(value.Permissions);
        if (value.InviteMaxUses is < 1 or > 100 || value.InviteDefaultUses < 1 || value.InviteDefaultUses > value.InviteMaxUses || value.InviteMaxExpiryMinutes is < 15 or > 43200 || value.InviteDefaultExpiryMinutes < 15 || value.InviteDefaultExpiryMinutes > value.InviteMaxExpiryMinutes || value.WatchPartyUses is < 1 or > 100 || value.WatchPartyExpiryMinutes is < 1 or > 1440) throw new ArgumentException("Invalid invitation defaults or limits.");
        if (!string.IsNullOrEmpty(value.PublicServerUrl) && (!Uri.TryCreate(value.PublicServerUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)) throw new ArgumentException("Public server URL must use HTTPS without credentials, query or fragment.");
        if (value.Enabled && !Snowflake(value.GuildId)) throw new ArgumentException("Enabled bot requires a guild. Public HTTPS URL is only needed for Jellyfin invitation links.");
        if (value.AllowedChannelIds is null || value.AllowedChannelIds.Length > 50 || value.AllowedChannelIds.Any(id => !Snowflake(id))) throw new ArgumentException("Invalid channel allowlist.");
        if (value.LibraryChannelIds is null || value.LibraryChannelIds.Length > 50 || value.LibraryChannelIds.Any(id => !Snowflake(id) || !value.AllowedChannelIds.Contains(id)) || value.LibraryIds is null || value.LibraryIds.Length > 100 || value.LibraryIds.Any(id => id == Guid.Empty)) throw new ArgumentException("Invalid library announcement scope or channels.");
        if (value.LibraryAnnouncementsEnabled && (!value.LibraryDisclosureAcknowledged || value.LibraryChannelIds.Length == 0 || value.LibraryIds.Length == 0)) throw new ArgumentException("Library announcements require explicit disclosure consent and selected libraries/approved channels.");
    }
}
