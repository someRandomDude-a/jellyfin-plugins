using System.Text.Json;
using AccountHub.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace AccountHub.Notifications;

/// <summary>Module-owned tables in the Hub database. Never use a second audit writer.</summary>
public sealed partial class NotificationStore
{
    private readonly string connectionString;
    private readonly IDataProtector protector;
    private readonly object gate = new();
    private readonly TimeProvider time;
    private readonly bool requireExistingDatabase;
    public bool Ready { get; private set; } = true;

    public NotificationStore(NotificationOptions options, IDataProtectionProvider protection, TimeProvider time)
    {
        this.time = time;
        requireExistingDatabase = options.RequireExistingDatabase;
        protector = protection.CreateProtector("AccountHub.Notifications.v1");
        connectionString = new SqliteConnectionStringBuilder { DataSource = options.DatabasePath, DefaultTimeout = 10 }.ToString();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!);
            Run(c =>
            {
                using(var moved=Command(c,"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='nh_moved'"))
                    if(Convert.ToInt32(moved.ExecuteScalar())!=0)throw new InvalidOperationException("Notifications moved to their standalone provider; legacy store must not be reopened");
                Execute(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
                using var transaction = c.BeginTransaction(deferred: false);
                Execute(c, "CREATE TABLE IF NOT EXISTS nh_schema(version INTEGER NOT NULL); INSERT INTO nh_schema SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM nh_schema);");
                using (var version = Command(c, "SELECT version FROM nh_schema"))
                    if (Convert.ToInt32(version.ExecuteScalar()) != 1) throw new InvalidOperationException("Unsupported notification schema");
                Execute(c, """
                CREATE TABLE IF NOT EXISTS nh_events(source TEXT NOT NULL,event_id TEXT NOT NULL,hash TEXT NOT NULL,logical_key TEXT NOT NULL,result TEXT NOT NULL,created INTEGER NOT NULL,PRIMARY KEY(source,event_id));
                CREATE TABLE IF NOT EXISTS nh_event_content(source TEXT NOT NULL,event_id TEXT NOT NULL,event TEXT NOT NULL,created INTEGER NOT NULL,PRIMARY KEY(source,event_id));
                CREATE TABLE IF NOT EXISTS nh_logical(source TEXT NOT NULL,logical_key TEXT NOT NULL,result TEXT NOT NULL,created INTEGER NOT NULL,PRIMARY KEY(source,logical_key));
                CREATE TABLE IF NOT EXISTS nh_inbox(id TEXT PRIMARY KEY,user_id TEXT NOT NULL,event TEXT NOT NULL,is_read INTEGER NOT NULL DEFAULT 0,dismissed INTEGER NOT NULL DEFAULT 0,created INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS nh_inbox_user ON nh_inbox(user_id,created);
                CREATE TABLE IF NOT EXISTS nh_deliveries(id TEXT PRIMARY KEY,user_id TEXT NOT NULL,admin_only INTEGER NOT NULL,event TEXT NOT NULL,route TEXT NOT NULL,body TEXT NOT NULL,discord_link TEXT,status TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,lease TEXT,lease_until INTEGER,next_at INTEGER NOT NULL,error TEXT,message_id TEXT,created INTEGER NOT NULL,terminal INTEGER);
                CREATE INDEX IF NOT EXISTS nh_due ON nh_deliveries(status,next_at);
                CREATE TABLE IF NOT EXISTS nh_preferences(user_id TEXT PRIMARY KEY,json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS nh_settings(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL,json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS nh_tokens(hash TEXT PRIMARY KEY,source TEXT NOT NULL,scopes TEXT NOT NULL,revoked INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS nh_receipts(hash TEXT PRIMARY KEY,event TEXT,audit TEXT NOT NULL,status TEXT NOT NULL,created INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS nh_push_settings(id INTEGER PRIMARY KEY CHECK(id=1),json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS nh_push_subscriptions(id TEXT PRIMARY KEY,user_id TEXT NOT NULL,json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS nh_push_jobs(id TEXT PRIMARY KEY,user_id TEXT NOT NULL,notification_id TEXT NOT NULL,event_id TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,next_at INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS nh_suppressed(id TEXT PRIMARY KEY,user_id TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS nh_suppressed_user ON nh_suppressed(user_id);
                """);
                transaction.Commit();
                return 0;
            });
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { Ready = false; }
    }
    private long Now => time.GetUtcNow().ToUnixTimeSeconds();
    private T Run<T>(Func<SqliteConnection, T> action)
    {
        lock (gate)
        {
            if (!Ready) throw new NotificationException("unavailable", "Notification storage unavailable", 503);
            var builder = new SqliteConnectionStringBuilder(connectionString);
            if (requireExistingDatabase || File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(builder.DataSource))!, "installation.id"))) builder.Mode = SqliteOpenMode.ReadWrite;
            using var c = new SqliteConnection(builder.ToString());
            c.Open();
            Execute(c, "PRAGMA synchronous=FULL;");
            return action(c);
        }
    }
    private static SqliteCommand Command(SqliteConnection c, string sql, params object?[] args)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
        return cmd;
    }
    private static int Execute(SqliteConnection c, string sql, params object?[] args)
    { using var cmd = Command(c, sql, args); return cmd.ExecuteNonQuery(); }
    private static string? Scalar(SqliteConnection c, string sql, params object?[] args)
    { using var cmd = Command(c, sql, args); return cmd.ExecuteScalar()?.ToString(); }
    private string Encrypt(object value) => protector.Protect(Contract.Serialize(value));
    private T Decrypt<T>(string value) => JsonSerializer.Deserialize<T>(protector.Unprotect(value), Contract.Json)!;

    public PublishResult? TryDuplicate(NotificationEvent entry, string hash, bool upsert = false) => Run(c =>
    {
        using var tx = c.BeginTransaction(deferred: false);
        var oldHash = Scalar(c, "SELECT hash FROM nh_events WHERE source=$0 AND event_id=$1", entry.Source, entry.EventId);
        if (oldHash is not null)
        {
            if (oldHash != hash) throw new NotificationException("rejected", "event_conflict", 409);
            return JsonSerializer.Deserialize<PublishResult>(Scalar(c, "SELECT result FROM nh_events WHERE source=$0 AND event_id=$1", entry.Source, entry.EventId)!, Contract.Json)! with { Status = "duplicate" };
        }
        var key = entry.DedupeKey ?? entry.EventId.ToString();
        if (upsert) return null;
        var prior = Scalar(c, "SELECT result FROM nh_logical WHERE source=$0 AND logical_key=$1", entry.Source, key);
        if (prior is null) return null;
        var result = JsonSerializer.Deserialize<PublishResult>(prior, Contract.Json)! with { Status = "duplicate" };
        Execute(c, "INSERT INTO nh_events VALUES($0,$1,$2,$3,$4,$5)", entry.Source, entry.EventId, hash, key, Contract.Serialize(result), Now);
        Execute(c, "INSERT INTO nh_event_content VALUES($0,$1,$2,$3)", entry.Source, entry.EventId, Contract.Serialize(entry), Now);
        tx.Commit(); return result;
    });

    public PublishResult Accept(NotificationEvent entry, string hash, IReadOnlyList<(Guid User, bool Inbox, Route[] Routes, string? Link)> fanout, bool upsert = false)
        => Run(c =>
        {
            using var transaction = c.BeginTransaction(deferred: false);
            var oldHash = Scalar(c, "SELECT hash FROM nh_events WHERE source=$0 AND event_id=$1", entry.Source, entry.EventId);
            if (oldHash is not null)
            {
                if (oldHash != hash) throw new NotificationException("rejected", "event_conflict", 409);
                var old = JsonSerializer.Deserialize<PublishResult>(Scalar(c, "SELECT result FROM nh_events WHERE source=$0 AND event_id=$1", entry.Source, entry.EventId)!, Contract.Json)!;
                return old with { Status = "duplicate" };
            }
            var key = entry.DedupeKey ?? entry.EventId.ToString();
            var prior = Scalar(c, "SELECT result FROM nh_logical WHERE source=$0 AND logical_key=$1", entry.Source, key);
            PublishResult result;
            if (prior is not null && !upsert) result = JsonSerializer.Deserialize<PublishResult>(prior, Contract.Json)! with { Status = "duplicate" };
            else
            {
                var ids = new List<Guid>();
                var configJson=Scalar(c,"SELECT json FROM nh_settings WHERE id=1");var presentation=configJson is null?new Settings(0,[]):Decrypt<Settings>(configJson);
                foreach (var recipient in fanout)
                {
                    if (recipient.Inbox||presentation.BlockedNotificationLimit>0)
                    {
                        var existing = upsert ? Scalar(c, "SELECT id FROM nh_inbox WHERE user_id=$0 AND json_extract(event,'$.source')=$1 AND json_extract(event,'$.dedupeKey')=$2 AND json_extract(event,'$.adminOnly')=$3 ORDER BY created DESC LIMIT 1", recipient.User, entry.Source, key, entry.AdminOnly) : null;
                        var id = existing is null ? Guid.NewGuid() : Guid.Parse(existing); if(recipient.Inbox)ids.Add(id);
                        var update=true;
                        if (existing is null) Execute(c, "INSERT INTO nh_inbox(id,user_id,event,created) VALUES($0,$1,$2,$3)", id, recipient.User, Contract.Serialize(entry), Now);
                        else {
                            var previous = JsonSerializer.Deserialize<NotificationEvent>(Scalar(c, "SELECT event FROM nh_inbox WHERE id=$0", id)!, Contract.Json)!;
                            update=entry.OccurredAt>=previous.OccurredAt;
                            if (update) Execute(c, "UPDATE nh_inbox SET rowid=(SELECT COALESCE(MAX(rowid),0)+1 FROM nh_inbox),event=$1,created=$2,is_read=0,dismissed=0 WHERE id=$0", id, Contract.Serialize(entry), Now);
                        }
                        if(update){if(recipient.Inbox)Execute(c,"DELETE FROM nh_suppressed WHERE id=$0",id);else{Execute(c,"INSERT OR IGNORE INTO nh_suppressed VALUES($0,$1)",id,recipient.User);TrimSuppressed(c,recipient.User,presentation);}}
                        var push=Scalar(c,"SELECT json FROM nh_push_settings WHERE id=1");
                        if(recipient.Inbox&&update&&push is not null&&Decrypt<PushConfiguration>(push).Enabled&&Scalar(c,"SELECT id FROM nh_push_subscriptions WHERE user_id=$0 LIMIT 1",recipient.User) is not null&&int.Parse(Scalar(c,"SELECT COUNT(*) FROM nh_push_jobs")!)<10000)
                            Execute(c,"INSERT OR IGNORE INTO nh_push_jobs(id,user_id,notification_id,event_id,next_at) VALUES($0,$1,$2,$3,$4)",Validation.Hash(id+":"+entry.EventId),recipient.User,id,entry.EventId,Now);
                    }
                    foreach (var route in prior is not null && upsert ? Array.Empty<Route>() : recipient.Routes)
                    {
                        var id = Guid.NewGuid();
                        // The immutable body is reused for ambiguous retries. Destinations/secrets are protected.
                        var body = Contract.Serialize(new
                        {
                            schemaVersion = 1,
                            deliveryId = id,
                            eventId = entry.EventId,
                            entry.Source,
                            entry.Type,
                            entry.Severity,
                            entry.OccurredAt,
                            title = route.DirectMessage ? entry.Title : "Account Hub notification",
                            body = route.DirectMessage ? entry.Body : "Open Account Hub to review this event."
                        });
                        Execute(c, """
                            INSERT INTO nh_deliveries(id,user_id,admin_only,event,route,body,discord_link,status,next_at,created)
                            VALUES($0,$1,$2,$3,$4,$5,$6,'pending',$7,$7)
                            """, id, recipient.User, entry.AdminOnly, Contract.Serialize(entry), Encrypt(route), body, recipient.Link, Now);
                    }
                }
                result = new("accepted", ids.ToArray());
                Execute(c, "INSERT OR REPLACE INTO nh_logical VALUES($0,$1,$2,$3)", entry.Source, key, Contract.Serialize(result), Now);
            }
            Execute(c, "INSERT INTO nh_events VALUES($0,$1,$2,$3,$4,$5)", entry.Source, entry.EventId, hash, key, Contract.Serialize(result), Now);
            Execute(c, "INSERT INTO nh_event_content VALUES($0,$1,$2,$3)", entry.Source, entry.EventId, Contract.Serialize(entry), Now);
            transaction.Commit();
            return result;
        });

    public IReadOnlyList<InboxRow> Inbox(Guid user, int skip, int limit, string? source, bool unread, bool includeAdmin = true, bool history = false,bool blocked=false) => Run(c =>
    {
        TrimSuppressed(c,user);
        ArchiveViewed(c,user);
        using var cmd = Command(c, """
            SELECT id,event,is_read,dismissed FROM nh_inbox WHERE user_id=$0 AND dismissed=$6
            AND ($1=0 OR is_read=0) AND ($2 IS NULL OR json_extract(event,'$.source')=$2)
            AND ($5=1 OR json_extract(event,'$.adminOnly')=0)
            AND EXISTS(SELECT 1 FROM nh_suppressed s WHERE s.id=nh_inbox.id)=$7
            ORDER BY created DESC,rowid DESC LIMIT $3 OFFSET $4
            """, user, unread, source, Math.Clamp(limit, 1,blocked?1000:100), Math.Clamp(skip, 0, 100000), includeAdmin, history,blocked);
        using var r = cmd.ExecuteReader(); var rows = new List<InboxRow>();
        while (r.Read()) rows.Add(new(Guid.Parse(r.GetString(0)), JsonSerializer.Deserialize<NotificationEvent>(r.GetString(1), Contract.Json)! with { Audience = [] }, r.GetBoolean(2), r.GetBoolean(3)));
        return rows;
    });
    public NotificationEvent? InboxEvent(Guid user, Guid id, bool includeAdmin) => Run(c => {
        ArchiveViewed(c,user);
        var json = Scalar(c, "SELECT event FROM nh_inbox WHERE id=$0 AND user_id=$1 AND dismissed=0 AND NOT EXISTS(SELECT 1 FROM nh_suppressed s WHERE s.id=nh_inbox.id) AND ($2=1 OR json_extract(event,'$.adminOnly')=0)", id, user, includeAdmin);
        return json is null ? null : JsonSerializer.Deserialize<NotificationEvent>(json, Contract.Json);
    });
    private void ArchiveViewed(SqliteConnection c,Guid user)=>Execute(c,"""
        UPDATE nh_inbox SET dismissed=1,is_read=1 WHERE user_id=$0 AND dismissed=0
        AND NOT EXISTS(SELECT 1 FROM nh_suppressed s WHERE s.id=nh_inbox.id)
        AND json_type(event,'$.layout.expireAfterViewHours')='integer'
        AND json_extract(event,'$.layout.expireAfterViewHours') BETWEEN 1 AND 8760
        AND strftime('%s',json_extract(event,'$.firstViewedAt'))+json_extract(event,'$.layout.expireAfterViewHours')*3600 <= $1
        """,user,Now);
    public long Unread(Guid user, bool includeAdmin = true) => Run(c => {ArchiveViewed(c,user);return long.Parse(Scalar(c, "SELECT count(*) FROM nh_inbox WHERE user_id=$0 AND is_read=0 AND dismissed=0 AND NOT EXISTS(SELECT 1 FROM nh_suppressed s WHERE s.id=nh_inbox.id) AND ($1=1 OR json_extract(event,'$.adminOnly')=0)", user, includeAdmin)!);});
    public bool Mark(Guid user, Guid id, bool dismiss) => Run(c => Execute(c, dismiss
        ? "UPDATE nh_inbox SET dismissed=1,is_read=1 WHERE id=$0 AND user_id=$1"
        : "UPDATE nh_inbox SET is_read=1,event=json_set(event,'$.firstViewedAt',COALESCE(json_extract(event,'$.firstViewedAt'),$2)) WHERE id=$0 AND user_id=$1 AND dismissed=0", id, user,time.GetUtcNow().ToString("O")) > 0);
    public void ReadAll(Guid user) => Run(c => Execute(c, "UPDATE nh_inbox SET is_read=1,event=json_set(event,'$.firstViewedAt',COALESCE(json_extract(event,'$.firstViewedAt'),$1)) WHERE id IN (SELECT id FROM nh_inbox WHERE user_id=$0 AND is_read=0 AND dismissed=0 AND NOT EXISTS(SELECT 1 FROM nh_suppressed s WHERE s.id=nh_inbox.id) LIMIT 1000)", user,time.GetUtcNow().ToString("O")));
    public void RestoreBlocked(Guid user,Guid id,Guid eventId)=>Run(c=>{using var tx=c.BeginTransaction();Execute(c,"UPDATE nh_inbox SET event=json_set(event,'$.retroactive',json('true')) WHERE id=$0 AND user_id=$1 AND json_extract(event,'$.eventId')=$2 AND EXISTS(SELECT 1 FROM nh_suppressed s WHERE s.id=nh_inbox.id)",id,user,eventId.ToString("D"));Execute(c,"DELETE FROM nh_suppressed WHERE id=$0 AND user_id=$1 AND EXISTS(SELECT 1 FROM nh_inbox i WHERE i.id=$0 AND json_extract(i.event,'$.eventId')=$2)",id,user,eventId.ToString("D"));tx.Commit();return 0;});
    private void TrimSuppressed(SqliteConnection c,Guid user,Settings? settings=null){
        if(Scalar(c,"SELECT id FROM nh_suppressed WHERE user_id=$0 LIMIT 1",user) is null)return;
        if(settings is null){var json=Scalar(c,"SELECT json FROM nh_settings WHERE id=1");settings=json is null?new(0,[]):Decrypt<Settings>(json);}
        Execute(c,"DELETE FROM nh_inbox WHERE user_id=$0 AND id IN(SELECT id FROM nh_suppressed WHERE user_id=$0) AND created<$1",user,Now-settings.NotificationDays*86400L);
        Execute(c,"DELETE FROM nh_inbox WHERE id IN(SELECT i.id FROM nh_inbox i JOIN nh_suppressed s ON i.id=s.id WHERE i.user_id=$0 ORDER BY i.created DESC,i.rowid DESC LIMIT -1 OFFSET $1)",user,settings.BlockedNotificationLimit);
        Execute(c,"DELETE FROM nh_suppressed WHERE user_id=$0 AND NOT EXISTS(SELECT 1 FROM nh_inbox i WHERE i.id=nh_suppressed.id)",user);
    }
    public Preference[] Preferences(Guid user) => Run(c => {
        var saved=JsonSerializer.Deserialize<Preference[]>(Scalar(c,"SELECT json FROM nh_preferences WHERE user_id=$0",user)??"[]",Contract.Json)!;
        // Retain old personal library choices after the observer moves owners.
        // Explicit Notifications rules win; unrelated Hub preferences are not copied.
        bool Library(Preference p) => p.Type.StartsWith("library.",StringComparison.Ordinal)||p.Type.StartsWith("category.library.",StringComparison.Ordinal);
        var canonical=saved.Where(p=>p.Source!="account-hub"||!Library(p)||!saved.Any(n=>n.Source=="notifications"&&n.Type==p.Type&&n.Channel==p.Channel)).ToArray();
        return canonical.Concat(canonical.Where(p=>p.Source is "account-hub" or "notifications"&&Library(p))
            .Select(p=>p with{Source=p.Source=="account-hub"?"notifications":"account-hub"})).ToArray();
    });
    public string LiveDigest(Guid user)=>Run(c=>{ArchiveViewed(c,user);using var cmd=Command(c,"SELECT id,json_extract(event,'$.eventId'),is_read,dismissed FROM nh_inbox WHERE user_id=$0 ORDER BY created DESC,rowid DESC LIMIT 1000",user);using var rows=cmd.ExecuteReader();var text=new System.Text.StringBuilder();while(rows.Read())text.Append(rows.GetString(0)).Append(':').Append(rows.IsDBNull(1)?"":rows.GetString(1)).Append(':').Append(rows.GetInt64(2)).Append(':').Append(rows.GetInt64(3)).Append(';');return Validation.Hash(text.ToString());});
    public void Preferences(Guid user, Preference[] preferences)
    {
        Validation.Require(preferences.Length <= 100 && preferences.All(p => p is not null && p.Source?.Length is > 0 and <= 100 && p.Type?.Length is > 0 and <= 100 && (p.Channel is "in-app" or "discord")&&(p.Importance is null or "silent" or "low" or "normal" or "high")), "Invalid preferences");
        Run(c => Execute(c, "INSERT INTO nh_preferences VALUES($0,$1) ON CONFLICT(user_id) DO UPDATE SET json=excluded.json", user, Contract.Serialize(preferences)));
    }
    public void SetPreference(Guid user, Preference rule)
    {
        Validation.Require(rule is not null && rule.Source?.Length is > 0 and <= 100 && rule.Type?.Length is > 0 and <= 100 && (rule.Channel is "in-app" or "discord")&&(rule.Importance is null or "silent" or "low" or "normal" or "high"), "Invalid preference");
        Run(db => {
            using var tx = db.BeginTransaction(deferred: false);
            var current = JsonSerializer.Deserialize<Preference[]>(Scalar(db, "SELECT json FROM nh_preferences WHERE user_id=$0", user) ?? "[]", Contract.Json)!;
            var libraryRule=rule.Source is "notifications" or "account-hub"&&(rule.Type.StartsWith("library.",StringComparison.Ordinal)||rule.Type.StartsWith("category.library.",StringComparison.Ordinal));
            var updated = current.Where(p => !(p.Type == rule.Type && p.Channel == rule.Channel && (p.Source == rule.Source || libraryRule && p.Source is "account-hub" or "notifications"))).Append(libraryRule?rule with{Source="notifications"}:rule).ToArray();
            Validation.Require(updated.Length <= 100, "Too many preferences");
            Execute(db, "INSERT INTO nh_preferences VALUES($0,$1) ON CONFLICT(user_id) DO UPDATE SET json=excluded.json", user, Contract.Serialize(updated));
            tx.Commit(); return 0;
        });
    }
    public Settings Settings(bool redacted = true) => Run(c =>
    {
        var data = Scalar(c, "SELECT json FROM nh_settings WHERE id=1");
        var settings = data is null ? new Settings(0, []) : Decrypt<Settings>(data);
        return redacted ? settings with { Routes = settings.Routes.Select(r => r with { Destination = "[configured]", Secret = null }).ToArray() } : settings;
    });
    public void SaveSettings(Settings settings) => Run(c =>
    {
        using var tx = c.BeginTransaction(deferred: false);
        var revision = long.Parse(Scalar(c, "SELECT revision FROM nh_settings WHERE id=1") ?? "0");
        if (revision != settings.Revision) throw new NotificationException("revision_changed", "Reload settings", 409);
        Execute(c, "INSERT INTO nh_settings VALUES(1,$0,$1) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,json=excluded.json",
            revision + 1, Encrypt(settings with { Revision = revision + 1 }));
        tx.Commit(); return 0;
    });
    public string IssueToken(string source, string[] scopes)
    {
        Validation.Require(source != "jellyfin-security" && source.Length is > 0 and <= 100 && scopes.Length > 0 && scopes.All(s => s is "notifications:write" or "audit:write" or "notifications:admin"), "Invalid credential");
        var raw = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Run(c => Execute(c, "INSERT INTO nh_tokens(hash,source,scopes) VALUES($0,$1,$2)", Validation.Hash(raw), source, Contract.Serialize(scopes)));
        return raw;
    }
    public ProducerIdentity? Authenticate(string token) => Run(c =>
    {
        using var cmd = Command(c, "SELECT source,scopes FROM nh_tokens WHERE hash=$0 AND revoked=0", Validation.Hash(token));
        using var r = cmd.ExecuteReader(); return r.Read() ? new ProducerIdentity(r.GetString(0), JsonSerializer.Deserialize<string[]>(r.GetString(1), Contract.Json)!) : null;
    });
    public void RevokeToken(string hash) => Run(c => Execute(c, "UPDATE nh_tokens SET revoked=1 WHERE hash=$0", hash));

    public Delivery? Claim() => Run(c =>
    {
        using var tx = c.BeginTransaction(deferred: false);
        Execute(c, "UPDATE nh_deliveries SET status='pending',lease=NULL WHERE status='delivering' AND lease_until<=$0", Now);
        using var cmd = Command(c, "SELECT id,user_id,admin_only,event,route,body,attempts,discord_link FROM nh_deliveries WHERE status IN ('pending','failed') AND next_at<=$0 ORDER BY next_at LIMIT 1", Now);
        Delivery? result;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            result = new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetBoolean(2),
                JsonSerializer.Deserialize<NotificationEvent>(r.GetString(3), Contract.Json)!, Decrypt<Route>(r.GetString(4)),
                r.GetInt32(6) + 1, Guid.NewGuid().ToString(), r.GetString(5), r.IsDBNull(7) ? null : r.GetString(7));
        }
        Execute(c, "UPDATE nh_deliveries SET status='delivering',lease=$0,lease_until=$1,attempts=attempts+1 WHERE id=$2", result.Lease, Now + 120, result.Id);
        tx.Commit(); return result;
    });
    public void Complete(Delivery delivery, SendResult result) => Run(c =>
    {
        using var tx = c.BeginTransaction(deferred: false);
        var status = result.Delivered ? "sent" : result.Retryable && delivery.Attempts < 6 ? "failed" : "dead_letter";
        var delays = new[] { 60, 300, 900, 3600, 21600, 21600 };
        var seconds = Math.Clamp(Math.Max(delays[Math.Min(delivery.Attempts - 1, 5)], result.RetryAfter?.TotalSeconds ?? 0), 1, 86400);
        var changed = Execute(c, """
            UPDATE nh_deliveries SET status=$0,next_at=$1,error=$2,message_id=$3,lease=NULL,terminal=$4
            WHERE id=$5 AND lease=$6 AND status='delivering'
            """, status, Now + (long)seconds, result.Delivered ? null : result.Retryable ? "transport_transient" : "transport_denied",
            result.MessageId, status is "sent" or "dead_letter" ? Now : null, delivery.Id, delivery.Lease);
        if (changed > 0 && status == "dead_letter")
        {
            var summary = new NotificationEvent(1, delivery.Id, "account-hub", "notifications.delivery.failed", "high",
                "Notification delivery needs attention", "Open notification delivery health to inspect a failed route.", [], true,
                delivery.Event.OccurredAt, DedupeKey: "delivery-failure:" + delivery.Id);
            var audit = new AuditEvent(1, delivery.Id, "account-hub", "notifications.delivery.failed", "failure", delivery.Event.OccurredAt);
            Execute(c, "INSERT OR IGNORE INTO nh_receipts VALUES($0,$1,$2,'pending',$3)", "failure:" + delivery.Id, Contract.Serialize(summary), Contract.Serialize(audit), Now);
        }
        tx.Commit(); return changed;
    });
    public object[] Deliveries(int skip, int limit) => Run(c =>
    {
        using var cmd = Command(c, "SELECT id,status,attempts,error,created FROM nh_deliveries ORDER BY created DESC,id LIMIT $0 OFFSET $1", Math.Clamp(limit, 1, 100), Math.Clamp(skip, 0, 100000));
        using var r = cmd.ExecuteReader(); var rows = new List<object>();
        while (r.Read()) rows.Add(new { id = r.GetString(0), status = r.GetString(1), attempts = r.GetInt32(2), error = r.IsDBNull(3) ? null : r.GetString(3), created = r.GetInt64(4) });
        return rows.ToArray();
    });
    public bool Retry(Guid id) => Run(c => Execute(c, "UPDATE nh_deliveries SET status='pending',attempts=0,next_at=$0,terminal=NULL WHERE id=$1 AND status='dead_letter'", Now, id) > 0);

    public bool Receipt(string hash, NotificationEvent? entry, AuditEvent audit, bool quarantine) => Run(c =>
    {
        // Bound quarantine under a valid but flooding sender. No raw Security body is ever retained.
        if (Scalar(c, "SELECT hash FROM nh_receipts WHERE hash=$0", hash) is not null) return false;
        if (quarantine && long.Parse(Scalar(c, "SELECT count(*) FROM nh_receipts WHERE status='quarantine'")!) >= 1000)
            throw new NotificationException("security_incompatible", "Quarantine full", 409);
        if (!quarantine && long.Parse(Scalar(c, "SELECT count(*) FROM nh_receipts WHERE status='pending'")!) >= 10000)
            throw new NotificationException("unavailable", "Receipt capacity reached", 503);
        return Execute(c, "INSERT OR IGNORE INTO nh_receipts VALUES($0,$1,$2,$3,$4)", hash,
            entry is null ? null : Contract.Serialize(entry), Contract.Serialize(audit), quarantine ? "quarantine" : "pending", Now) > 0;
    });
    public (string Hash, NotificationEvent? Event, AuditEvent? Audit)[] PendingReceipts() => Run(c =>
    {
        using var cmd = Command(c, "SELECT hash,event,audit FROM nh_receipts WHERE status='pending' ORDER BY created LIMIT 20");
        using var r = cmd.ExecuteReader(); var rows = new List<(string, NotificationEvent?, AuditEvent?)>();
        while (r.Read()) rows.Add((r.GetString(0), r.IsDBNull(1) ? null : JsonSerializer.Deserialize<NotificationEvent>(r.GetString(1), Contract.Json), JsonSerializer.Deserialize<AuditEvent>(r.GetString(2), Contract.Json)));
        return rows.ToArray();
    });
    public void AcknowledgeReceipt(string hash, bool audit) => Run(c =>
    {
        using var tx = c.BeginTransaction(deferred: false);
        Execute(c, audit ? "UPDATE nh_receipts SET audit='null' WHERE hash=$0 AND status='pending'" : "UPDATE nh_receipts SET event=NULL WHERE hash=$0 AND status='pending'", hash);
        Execute(c, "UPDATE nh_receipts SET status='done' WHERE hash=$0 AND event IS NULL AND audit='null' AND status='pending'", hash);
        tx.Commit(); return 0;
    });
    public void PrunePresentation(int days) => Run(c =>
    {
        using var tx = c.BeginTransaction(deferred: false);
        Execute(c, "DELETE FROM nh_inbox WHERE created<$0", Now - days * 86400L);
        Execute(c,"DELETE FROM nh_suppressed WHERE NOT EXISTS(SELECT 1 FROM nh_inbox i WHERE i.id=nh_suppressed.id)");
        Execute(c, "DELETE FROM nh_event_content WHERE created<$0", Now - days * 86400L);
        Execute(c, "DELETE FROM nh_deliveries WHERE terminal<$0", Now - 30 * 86400L);
        Execute(c, "DELETE FROM nh_receipts WHERE status!='pending' AND created<$0", Now - 90 * 86400L);
        // 400-day producer retry horizon survives presentation pruning.
        Execute(c, "DELETE FROM nh_events WHERE created<$0", Now - 400 * 86400L);
        Execute(c, "DELETE FROM nh_logical WHERE created<$0", Now - 400 * 86400L);
        tx.Commit(); return 0;
    });
}
