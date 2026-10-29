using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AccountHub.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Notifications;

/// <summary>One-way, checkpointed relocation of plugin-owned notification data only.</summary>
public static class NotificationsMigration
{
    private static readonly string[] Tables=["nh_schema","nh_events","nh_event_content","nh_logical","nh_inbox","nh_deliveries","nh_preferences","nh_settings","nh_tokens","nh_receipts"];
    private static readonly string[] OptionalTables=["nh_push_settings","nh_push_subscriptions","nh_push_jobs"];
    private static IEnumerable<string> ExistingTables(SqliteConnection db)=>Tables.Concat(OptionalTables.Concat(new[]{"nh_suppressed"}).Where(t=>Has(db,t)));
    private const string Purpose="AccountHub.Notifications.v1";
    public static string Initialize(string configurations)=>Initialize(configurations,null);
    internal static string Initialize(string configurations,Action<string>? checkpoint)
    {
        var root=Path.Combine(configurations,"Notifications");Directory.CreateDirectory(root);
        using var lease=new FileStream(Path.Combine(root,"migration.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var database=Path.Combine(root,"notifications.db");var stage=Path.Combine(root,"notifications.migrating.db");
        var source=Path.Combine(configurations,"AccountHub","account-hub.db");
        var intent=Path.Combine(root,"migration.intent");var complete=Path.Combine(root,"storage-v1.complete");
        if(File.Exists(complete))
        {
            if(File.ReadAllText(complete)!="1")throw new InvalidDataException("Unknown notification installation version");
            if(!File.Exists(database))throw new InvalidDataException("Restore original notification database; no store was recreated");
            RequireKeys(root);using var db=Open(database,SqliteOpenMode.ReadWrite);Validate(db);
            ValidateSecrets(db,Provider(root,"Notifications").CreateProtector(Purpose));
            if(State(db).Status!="complete")throw new InvalidDataException("Notification cutover is incomplete");
            Cleanup(root,intent,stage);
            return database; // Stable state never opens Hub's database or keys.
        }
        if(File.Exists(database))
        {
            RequireKeys(root);using var existing=Open(database,SqliteOpenMode.ReadWrite);Validate(existing);
            ValidateSecrets(existing,Provider(root,"Notifications").CreateProtector(Purpose));
            var state=State(existing);
            if(state.Status is null)
            {
                // Upgrade an already independent pre-migration Notifications installation.
                ValidateSecrets(existing,Provider(root,"Notifications").CreateProtector(Purpose));
                Checkpoint(existing,"complete","");
            }
            else if(state.Status=="copied")
            {
                RetireSource(source,state.Hash);
                Checkpoint(existing,"complete",state.Hash);
            }
            else if(state.Status!="complete")throw new InvalidDataException("Unknown migration checkpoint");
            Durable(complete,"1");Cleanup(root,intent,stage);return database;
        }
        if(File.Exists(Path.Combine(root,"storage-owner.txt")) && File.ReadAllText(Path.Combine(root,"storage-owner.txt"))=="own")
            throw new InvalidDataException("Restore the original Notifications database; it was not recreated");
        string mode;
        if(File.Exists(intent)){mode=File.ReadAllText(intent);if(mode is not ("legacy" or "fresh"))throw new InvalidDataException("Unknown migration intent");}
        else
        {
            if(Directory.Exists(Path.Combine(root,"keys"))&&Directory.EnumerateFiles(Path.Combine(root,"keys")).Any())throw new InvalidDataException("Notification database missing; restore original installation");
            mode=File.Exists(source)?"legacy":"fresh";Durable(intent,mode);
        }
        if(mode=="legacy"&&!File.Exists(source))throw new InvalidDataException("Legacy notification source missing during migration");
        // Only this migration's staging files may be discarded/rebuilt. No active DB is replaced.
        foreach(var suffix in new[]{"","-wal","-shm"})if(File.Exists(stage+suffix))File.Delete(stage+suffix);
        var protection=Provider(root,"Notifications");_=protection.CreateProtector("Notifications.Installation.1").Protect("initialized");
        var template=new NotificationStore(new(){DatabasePath=stage},protection,TimeProvider.System);
        if(!template.Ready)throw new InvalidDataException("Cannot initialize destination notification schema");
        SqliteConnection.ClearPool(new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=stage,DefaultTimeout=10}.ToString()));
        string hash="";
        using(var destination=Open(stage,SqliteOpenMode.ReadWrite))
        {
            Execute(destination,"PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;");
            if(mode=="legacy")
            {
                using var legacy=Open(source,SqliteOpenMode.ReadOnly);using var snapshot=legacy.BeginTransaction(deferred:true);
                if(Has(legacy,"nh_moved"))throw new InvalidDataException("Source was already migrated; restore the destination instead");
                if(Has(legacy,"nh_schema"))
                {
                    using(var known=legacy.CreateCommand()){known.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND substr(name,1,3)='nh_'";if(Convert.ToInt32(known.ExecuteScalar())!=ExistingTables(legacy).Count())throw new InvalidDataException("Unknown legacy notification tables");}
                    Validate(legacy);
                    var oldRoot=Path.Combine(configurations,"AccountHub");RequireKeys(oldRoot);
                    var oldProtector=Provider(oldRoot,"AccountHub").CreateProtector(Purpose);
                    var newProtector=protection.CreateProtector(Purpose);
                    using var tx=destination.BeginTransaction();
                    foreach(var table in ExistingTables(legacy))
                    {
                        if(Columns(legacy,table)!=Columns(destination,table))throw new InvalidDataException("Legacy notification schema mismatch");
                        Execute(destination,$"DELETE FROM {table}");
                        using var read=legacy.CreateCommand();read.Transaction=snapshot;read.CommandText=$"SELECT * FROM {table} ORDER BY rowid";
                        using var rows=read.ExecuteReader();
                        while(rows.Read())
                        {
                            using var write=destination.CreateCommand();write.Transaction=tx;
                            write.CommandText=$"INSERT INTO {table} VALUES({string.Join(',',Enumerable.Range(0,rows.FieldCount).Select(i=>"$p"+i))})";
                            for(var i=0;i<rows.FieldCount;i++)
                            {
                                object value=rows.GetValue(i);
                                if(value is string cipher && ((table is "nh_settings" or "nh_push_settings" or "nh_push_subscriptions")&&rows.GetName(i)=="json" || table=="nh_deliveries"&&rows.GetName(i)=="route"))
                                    value=newProtector.Protect(oldProtector.Unprotect(cipher));
                                write.Parameters.AddWithValue("$p"+i,value);
                            }
                            write.ExecuteNonQuery();
                        }
                    }
                    hash=Fingerprint(legacy);Validate(destination);ValidateSecrets(destination,newProtector);
                    Checkpoint(destination,"copied",hash);tx.Commit();
                }
                else
                {
                    using var known=legacy.CreateCommand();known.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND substr(name,1,3)='nh_'";
                    if(Convert.ToInt32(known.ExecuteScalar())!=0)throw new InvalidDataException("Incomplete legacy notification schema");
                    Checkpoint(destination,"complete","");
                }
                snapshot.Commit();
            }
            else Checkpoint(destination,"complete","");
            Validate(destination);
        }
        using(var file=new FileStream(stage,FileMode.Open,FileAccess.ReadWrite,FileShare.None))file.Flush(true);
        checkpoint?.Invoke("staged");
        File.Move(stage,database); // Atomic cutover; never overwrite an existing destination.
        checkpoint?.Invoke("cutover");
        if(hash.Length>0)RetireSource(source,hash);
        checkpoint?.Invoke("retired");
        using(var own=Open(database,SqliteOpenMode.ReadWrite))Checkpoint(own,"complete",hash);
        Durable(complete,"1");Cleanup(root,intent,stage);return database;
    }
    private static void RetireSource(string path,string expected)
    {
        using var source=Open(path,SqliteOpenMode.ReadWrite);using var tx=source.BeginTransaction(deferred:false);
        if(Has(source,"nh_moved"))
        {
            using var check=source.CreateCommand();check.CommandText="SELECT hash FROM nh_moved WHERE id=1";
            if(check.ExecuteScalar() as string!=expected)throw new InvalidDataException("Source retirement marker mismatch");
        }
        else
        {
            if(Fingerprint(source)!=expected)throw new InvalidDataException("Legacy data changed during cutover; no source data was removed");
            foreach(var table in ExistingTables(source).ToArray())Execute(source,$"DROP TABLE {table}");
            Execute(source,"CREATE TABLE nh_moved(id INTEGER PRIMARY KEY CHECK(id=1),hash TEXT NOT NULL)");
            using var mark=source.CreateCommand();mark.CommandText="INSERT INTO nh_moved VALUES(1,$hash)";mark.Parameters.AddWithValue("$hash",expected);mark.ExecuteNonQuery();
        }
        tx.Commit();
    }
    private static void Validate(SqliteConnection db)
    {
        using var check=db.CreateCommand();check.CommandText="PRAGMA quick_check";
        if(check.ExecuteScalar() as string!="ok")throw new InvalidDataException("Notification database integrity failure");
        foreach(var table in Tables)if(!Has(db,table))throw new InvalidDataException("Notification table missing");
        var optional=OptionalTables.Count(t=>Has(db,t));if(optional!=0&&optional!=OptionalTables.Length)throw new InvalidDataException("Push table missing; restore original state");
        check.CommandText="SELECT version FROM nh_schema";
        if(Convert.ToInt32(check.ExecuteScalar())!=1)throw new InvalidDataException("Unsupported notification schema");
    }
    private static void ValidateSecrets(SqliteConnection db,IDataProtector protector)
    {
        using var command=db.CreateCommand();command.CommandText="SELECT json FROM nh_settings UNION ALL SELECT route FROM nh_deliveries"+(Has(db,"nh_push_settings")?" UNION ALL SELECT json FROM nh_push_settings UNION ALL SELECT json FROM nh_push_subscriptions":"");
        using var rows=command.ExecuteReader();while(rows.Read())_=protector.Unprotect(rows.GetString(0));
    }
    private static string Fingerprint(SqliteConnection db)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var table in ExistingTables(db)){hash.AppendData(Encoding.UTF8.GetBytes(table));using var cmd=db.CreateCommand();cmd.CommandText=$"SELECT * FROM {table} ORDER BY rowid";using var rows=cmd.ExecuteReader();while(rows.Read()){var values=new object[rows.FieldCount];rows.GetValues(values);hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(values));}}
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static string Columns(SqliteConnection db,string table){using var cmd=db.CreateCommand();cmd.CommandText=$"PRAGMA table_info({table})";using var rows=cmd.ExecuteReader();var result=new List<string>();while(rows.Read())result.Add($"{rows.GetString(1)}:{rows.GetString(2)}:{rows.GetInt32(3)}:{rows.GetInt32(5)}");return string.Join('|',result);}
    private static bool Has(SqliteConnection db,string name){using var cmd=db.CreateCommand();cmd.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name";cmd.Parameters.AddWithValue("$name",name);return Convert.ToInt32(cmd.ExecuteScalar())==1;}
    private static (string? Status,string Hash) State(SqliteConnection db){if(!Has(db,"notifications_storage_migration"))return(null,"");using var cmd=db.CreateCommand();cmd.CommandText="SELECT status,hash FROM notifications_storage_migration WHERE id=1 AND version=1";using var row=cmd.ExecuteReader();if(!row.Read())throw new InvalidDataException("Unknown notification migration version");return(row.GetString(0),row.GetString(1));}
    private static void Checkpoint(SqliteConnection db,string status,string hash){Execute(db,"CREATE TABLE IF NOT EXISTS notifications_storage_migration(id INTEGER PRIMARY KEY CHECK(id=1),version INTEGER NOT NULL,status TEXT NOT NULL,hash TEXT NOT NULL)");using var cmd=db.CreateCommand();cmd.CommandText="INSERT INTO notifications_storage_migration VALUES(1,1,$status,$hash) ON CONFLICT(id) DO UPDATE SET status=excluded.status,hash=excluded.hash";cmd.Parameters.AddWithValue("$status",status);cmd.Parameters.AddWithValue("$hash",hash);cmd.ExecuteNonQuery();}
    private static SqliteConnection Open(string path,SqliteOpenMode mode){var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=mode,Pooling=false,DefaultTimeout=10}.ToString());try{db.Open();return db;}catch{db.Dispose();throw;}}
    private static void Execute(SqliteConnection db,string sql){using var cmd=db.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}
    private static IDataProtectionProvider Provider(string root,string name)=>DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root,"keys")),b=>b.SetApplicationName(name));
    private static void RequireKeys(string root){if(!Directory.Exists(Path.Combine(root,"keys"))||!Directory.EnumerateFiles(Path.Combine(root,"keys"),"key-*.xml").Any())throw new InvalidOperationException("Restore original notification encryption keys");}
    private static void Durable(string path,string value){if(File.Exists(path)){if(File.ReadAllText(path)!=value)throw new InvalidDataException("Migration marker mismatch");return;}using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);output.Write(Encoding.UTF8.GetBytes(value));output.Flush(true);}
    private static void Cleanup(string root,string intent,string stage){foreach(var path in new[]{intent,Path.Combine(root,"storage-owner.txt"),stage,stage+"-wal",stage+"-shm"})if(File.Exists(path))File.Delete(path);}
}
