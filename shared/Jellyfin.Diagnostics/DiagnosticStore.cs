using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Diagnostics;

public sealed record DiagnosticRecord(string Id,string Source,string Level,string Code,string Operation,DateTimeOffset CreatedAt,string? ExceptionType=null,string? Frames=null)
{
    public int? StatusCode { get; init; }
    public string? PluginVersion { get; init; }
    public bool IsFailure { get; init; }
}
public sealed record DiagnosticSettings(long Revision,string MinimumLevel);

// This writer owns only its separate diagnostics database, never Jellyfin or
// another plugin's identity, claims, delivery or removal records.
public sealed class DiagnosticStore(string directory)
{
    public static readonly string[] Levels=["debug","info","warning","error","critical"];
    private static readonly string[] Sources=["account-hub","notifications","discord-bridge","private-syncplay","deletion-queue","automatic-deletion-requests"];
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static string Code(string? code)=>code is {Length:>0 and <=100}&&code.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-' or '.')?code:"request_failed";
    private SqliteConnection Open(){
        Directory.CreateDirectory(directory);var file=Path.Combine(directory,"diagnostics.db");var marker=Path.Combine(directory,"diagnostics.installed");
        if(File.Exists(marker)&&!File.Exists(file))throw new InvalidOperationException("Original diagnostics database missing");
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=file,DefaultTimeout=5,Pooling=false}.ToString());
        try{db.Open();using var transaction=db.BeginTransaction(deferred:false);using var command=db.CreateCommand();command.Transaction=transaction;command.CommandText="PRAGMA user_version";var version=Convert.ToInt32(command.ExecuteScalar());if(version is not (0 or 1))throw new InvalidOperationException("Unknown diagnostics schema");
            if(version==0){command.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";if(Convert.ToInt32(command.ExecuteScalar())!=0)throw new InvalidOperationException("Unknown diagnostics schema");
                command.CommandText="CREATE TABLE IF NOT EXISTS diagnostics(id TEXT PRIMARY KEY,source TEXT NOT NULL,level TEXT NOT NULL,code TEXT NOT NULL,operation TEXT NOT NULL,created TEXT NOT NULL,json TEXT NOT NULL);CREATE INDEX IF NOT EXISTS diagnostics_created ON diagnostics(created);CREATE TABLE IF NOT EXISTS diagnostics_settings(id INTEGER PRIMARY KEY CHECK(id=1),revision INTEGER NOT NULL,minimum_level TEXT NOT NULL);INSERT OR IGNORE INTO diagnostics_settings VALUES(1,1,'info');PRAGMA user_version=1;";command.ExecuteNonQuery();}
            transaction.Commit();if(!File.Exists(marker)){try{using var identity=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.Read);identity.WriteByte((byte)'1');}catch(IOException)when(File.Exists(marker)){}}return db;
        }catch{db.Dispose();throw;}
    }
    public string Record(DiagnosticRecord value){
        if(!Guid.TryParseExact(value.Id,"D",out _)||!Sources.Contains(value.Source)||!Levels.Contains(value.Level))throw new ArgumentException();
        var safe=value with{Code=Code(value.Code),Operation=Clean(value.Operation,300),ExceptionType=value.ExceptionType is null?null:Clean(value.ExceptionType,200),Frames=value.Frames is null?null:Clean(value.Frames,4096),StatusCode=value.StatusCode is >=100 and <=599?value.StatusCode:null,PluginVersion=value.PluginVersion is {} version&&version.Length<=50&&version.All(c=>char.IsAsciiDigit(c)||c=='.')?version:null};
        using var db=Open();using var tx=db.BeginTransaction(deferred:false);using var command=db.CreateCommand();command.Transaction=tx;command.CommandText="SELECT minimum_level FROM diagnostics_settings WHERE id=1";var minimum=(string)command.ExecuteScalar()!;
        if(!Levels.Contains(minimum))throw new InvalidOperationException("Invalid diagnostic recording settings");
        if(value.IsFailure||Array.IndexOf(Levels,value.Level)>=Array.IndexOf(Levels,minimum)){command.CommandText="INSERT OR IGNORE INTO diagnostics VALUES($id,$source,$level,$code,$operation,$created,$json)";command.Parameters.AddWithValue("$id",value.Id);command.Parameters.AddWithValue("$source",value.Source);command.Parameters.AddWithValue("$level",value.Level);command.Parameters.AddWithValue("$code",safe.Code);command.Parameters.AddWithValue("$operation",safe.Operation);command.Parameters.AddWithValue("$created",value.CreatedAt.ToUniversalTime().ToString("O"));command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(safe,Json));command.ExecuteNonQuery();command.Parameters.Clear();command.CommandText="DELETE FROM diagnostics WHERE id IN (SELECT id FROM diagnostics ORDER BY created DESC,id DESC LIMIT -1 OFFSET 10000)";command.ExecuteNonQuery();}tx.Commit();return value.Id;
    }
    private static string Clean(string value,int length)=>new(value.Where(c=>!char.IsControl(c)||c=='\n').Take(length).ToArray());
    public DiagnosticRecord[] Search(string id,string source,string level,int skip){using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT json FROM diagnostics WHERE ($id='' OR id=$id) AND ($source='' OR source=$source) AND ($level='' OR level=$level) ORDER BY created DESC,id DESC LIMIT 100 OFFSET $skip";command.Parameters.AddWithValue("$id",id);command.Parameters.AddWithValue("$source",source);command.Parameters.AddWithValue("$level",level);command.Parameters.AddWithValue("$skip",skip);using var rows=command.ExecuteReader();var result=new List<DiagnosticRecord>();while(rows.Read())result.Add(JsonSerializer.Deserialize<DiagnosticRecord>(rows.GetString(0),Json)!);return result.ToArray();}
    public DiagnosticSettings Settings(){using var db=Open();using var command=db.CreateCommand();command.CommandText="SELECT revision,minimum_level FROM diagnostics_settings WHERE id=1";using var rows=command.ExecuteReader();if(!rows.Read()||rows.GetInt64(0)<1||!Levels.Contains(rows.GetString(1)))throw new InvalidOperationException();return new(rows.GetInt64(0),rows.GetString(1));}
    public bool Configure(long revision,string level){if(!Levels.Contains(level))throw new ArgumentException();using var db=Open();using var command=db.CreateCommand();command.CommandText="UPDATE diagnostics_settings SET revision=revision+1,minimum_level=$level WHERE id=1 AND revision=$revision";command.Parameters.AddWithValue("$level",level);command.Parameters.AddWithValue("$revision",revision);return command.ExecuteNonQuery()==1;}
    public void Clear(Guid[] ids){if(ids.Length is <1 or >100||ids.Contains(Guid.Empty))throw new ArgumentException();using var db=Open();using var tx=db.BeginTransaction();using var command=db.CreateCommand();command.Transaction=tx;command.CommandText="DELETE FROM diagnostics WHERE id=$id";var parameter=command.Parameters.Add("$id",SqliteType.Text);foreach(var id in ids){parameter.Value=id.ToString("D");command.ExecuteNonQuery();}tx.Commit();}
}
