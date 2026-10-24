using AccountHub.Api;
using AccountHub.Notifications;
using Jellyfin.Plugin.Notifications;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Moq;
using Xunit;

namespace Notifications.Tests;

public sealed class StorageTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"opencode","notifications-tests-"+Guid.NewGuid().ToString("N"));
    private Storage Create(){var paths=new Mock<IApplicationPaths>();paths.SetupGet(p=>p.PluginConfigurationsPath).Returns(root);return new(paths.Object);}
    private static NotificationStore Open(Storage storage)=>new(new(){DatabasePath=storage.Database},storage.Protection(),TimeProvider.System);
    [Fact] public void StandaloneStoreDoesNotRequireHubAndSurvivesRestart()
    {
        var storage=Create();var store=Open(storage);var user=Guid.NewGuid();
        store.Preferences(user,[new("*","*","in-app",false)]);
        Assert.Contains(Path.Combine("Notifications","notifications.db"),storage.Database);
        Assert.False(Open(Create()).Preferences(user).Single().Enabled);
    }
    [Fact] public void LegacyInboxIdsPreferencesAndEncryptedRoutesMigrateToIndependentStableStorage()
    {
        var legacy=Path.Combine(root,"AccountHub");Directory.CreateDirectory(legacy);
        var protection=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(legacy,"keys")),b=>b.SetApplicationName("AccountHub"));
        _=protection.CreateProtector("test").Protect("initialize");
        var database=Path.Combine(legacy,"account-hub.db");
        var old=new NotificationStore(new(){DatabasePath=database},protection,TimeProvider.System);
        var user=Guid.NewGuid();var entry=new NotificationEvent(1,Guid.NewGuid(),"deletion-queue","proposal.created","info","Existing notice","Preserve me",[user],false,DateTimeOffset.UtcNow);
        var route=new Route("old","*","*","webhook","https://example.invalid",Secret:"preserve-encrypted-secret");
        old.Accept(entry,"hash",[(user,true,new[]{route},null)]);
        var leased=old.Claim();Assert.NotNull(leased);
        old.Preferences(user,[new("*","*","in-app",false)]);
        old.SaveSettings(new(0,[route]));
        var id=old.Inbox(user,0,25,null,false).Single().Id;
        var storage=Create();var current=Open(storage);
        Assert.NotEqual(database,storage.Database);
        Assert.Contains(Path.Combine("Notifications","notifications.db"),storage.Database);
        Assert.Equal(id,current.Inbox(user,0,25,null,false).Single().Id);
        Assert.False(current.Preferences(user).Single().Enabled);
        Assert.Equal("preserve-encrypted-secret",current.Settings(false).Routes.Single().Secret);
        Assert.Null(current.Claim()); // Migration must not reset an in-flight delivery lease.
        using(var pending=System.Text.Json.JsonDocument.Parse(Contract.Serialize(current.Deliveries(0,25)))){
            Assert.Equal(leased.Id,pending.RootElement[0].GetProperty("id").GetGuid());
            Assert.Equal("delivering",pending.RootElement[0].GetProperty("status").GetString());
            Assert.Equal(1,pending.RootElement[0].GetProperty("attempts").GetInt32());
        }
        Assert.True(File.Exists(database));
        using(var source=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Mode=SqliteOpenMode.ReadOnly}.ToString())){
            source.Open();using var command=source.CreateCommand();command.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND name='nh_inbox'";Assert.Equal(0L,command.ExecuteScalar());
            command.CommandText="SELECT count(*) FROM nh_moved";Assert.Equal(1L,command.ExecuteScalar());
        }
        SqliteConnection.ClearAllPools();Directory.Delete(legacy,true);
        var restarted=Open(Create());
        Assert.Equal(id,restarted.Inbox(user,0,25,null,false).Single().Id);
        Assert.Equal("preserve-encrypted-secret",restarted.Settings(false).Routes.Single().Secret);
        Assert.False(File.Exists(Path.Combine(storage.DirectoryPath,"migration.intent")));
    }
    [Fact] public void LaterHubInstallationCannotRedirectAnExistingStandaloneStore()
    {
        var storage=Create();_=Open(storage);var original=storage.Database;
        Directory.CreateDirectory(Path.Combine(root,"AccountHub"));File.WriteAllText(Path.Combine(root,"AccountHub","account-hub.db"),"not the notification store");
        Assert.Equal(original,Create().Database);
    }
    [Fact] public void MissingBoundDatabaseIsNeverRecreated()
    {
        var storage=Create();_=Open(storage);SqliteConnection.ClearAllPools();File.Delete(storage.Database);
        Assert.Throws<InvalidDataException>(()=>Create().Database);
        Assert.False(File.Exists(storage.Database));
    }
    [Fact] public void MissingOriginalKeysFailClosed()
    {
        var storage=Create();_=Open(storage);Directory.Delete(Path.Combine(storage.DirectoryPath,"keys"),true);
        Assert.Throws<InvalidOperationException>(()=>Create().Protection());
    }
    [Theory]
    [InlineData("staged")]
    [InlineData("cutover")]
    [InlineData("retired")]
    public void InterruptedMigrationConvergesWithoutDuplicatingOrLosingSourceData(string phase)
    {
        var legacy=Path.Combine(root,"AccountHub");Directory.CreateDirectory(legacy);
        var protection=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(legacy,"keys")),b=>b.SetApplicationName("AccountHub"));
        _=protection.CreateProtector("test").Protect("initialize");
        var oldDb=Path.Combine(legacy,"account-hub.db");
        var source=new NotificationStore(new(){DatabasePath=oldDb},protection,TimeProvider.System);
        var user=Guid.NewGuid();source.Preferences(user,[new("*","*","in-app",false)]);
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=oldDb}.ToString())){
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText="CREATE TABLE unrelated_hub_data(value INTEGER); INSERT INTO unrelated_hub_data VALUES(123);";cmd.ExecuteNonQuery();
        }
        Assert.Throws<IOException>(()=>NotificationsMigration.Initialize(root,point=>{if(point==phase)throw new IOException("simulated interruption");}));
        var current=Open(Create());Assert.False(current.Preferences(user).Single().Enabled);
        Assert.Equal(Path.Combine(root,"Notifications","notifications.db"),NotificationsMigration.Initialize(root));
        Assert.False(File.Exists(Path.Combine(root,"Notifications","migration.intent")));
        Assert.False(File.Exists(Path.Combine(root,"Notifications","notifications.migrating.db")));
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=oldDb}.ToString())){
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT value FROM unrelated_hub_data";Assert.Equal(123L,cmd.ExecuteScalar());
        }
        var retired=new NotificationStore(new(){DatabasePath=oldDb},protection,TimeProvider.System);
        Assert.False(retired.Ready); // Old hosts cannot silently recreate notification tables.
    }
    [Theory]
    [InlineData("staged")]
    [InlineData("cutover")]
    public void ChangedSourceFailsClosedWithoutRetiringLegacyData(string phase)
    {
        var legacy=Path.Combine(root,"AccountHub");Directory.CreateDirectory(legacy);
        var protection=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(legacy,"keys")),b=>b.SetApplicationName("AccountHub"));
        _=protection.CreateProtector("test").Protect("initialize");
        var database=Path.Combine(legacy,"account-hub.db");
        var source=new NotificationStore(new(){DatabasePath=database},protection,TimeProvider.System);
        var user=Guid.NewGuid();source.Preferences(user,[new("*","*","in-app",false)]);
        Assert.Throws<InvalidDataException>(()=>NotificationsMigration.Initialize(root,point=>{
            if(point==phase)source.Preferences(user,[new("*","*","in-app",true)]);
        }));
        Assert.True(source.Preferences(user).Single().Enabled);
        Assert.Throws<InvalidDataException>(()=>NotificationsMigration.Initialize(root));
        Assert.False(File.Exists(Path.Combine(root,"Notifications","storage-v1.complete")));
        Assert.True(new NotificationStore(new(){DatabasePath=database},protection,TimeProvider.System).Ready);
    }
    [Theory]
    [InlineData("UPDATE nh_settings SET json='invalid-ciphertext'")]
    [InlineData("UPDATE nh_schema SET version=99")]
    [InlineData("DROP TABLE nh_schema")]
    public void InvalidSourceIsNotCutOverOrRetired(string corruption)
    {
        var legacy=Path.Combine(root,"AccountHub");Directory.CreateDirectory(legacy);
        var protection=DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(legacy,"keys")),b=>b.SetApplicationName("AccountHub"));
        _=protection.CreateProtector("test").Protect("initialize");
        var database=Path.Combine(legacy,"account-hub.db");
        var source=new NotificationStore(new(){DatabasePath=database},protection,TimeProvider.System);
        source.SaveSettings(new(0,[]));
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database}.ToString())){
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText=corruption;cmd.ExecuteNonQuery();
        }
        Assert.ThrowsAny<Exception>(()=>NotificationsMigration.Initialize(root));
        Assert.False(File.Exists(Path.Combine(root,"Notifications","notifications.db")));
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database}.ToString())){
            db.Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND name='nh_inbox'";Assert.Equal(1L,cmd.ExecuteScalar());
            cmd.CommandText="SELECT count(*) FROM sqlite_master WHERE type='table' AND name='nh_moved'";Assert.Equal(0L,cmd.ExecuteScalar());
        }
    }
    [Fact] public void CompletedStartupRemovesInterruptedCleanupWithoutReadingLegacyStorage()
    {
        var storage=Create();_=Open(storage);
        File.WriteAllText(Path.Combine(storage.DirectoryPath,"migration.intent"),"fresh");
        File.WriteAllText(Path.Combine(storage.DirectoryPath,"notifications.migrating.db"),"abandoned staging");
        File.WriteAllText(Path.Combine(storage.DirectoryPath,"storage-owner.txt"),"own");
        Directory.CreateDirectory(Path.Combine(root,"AccountHub"));File.WriteAllText(Path.Combine(root,"AccountHub","account-hub.db"),"unreadable legacy database");
        Assert.Equal(storage.Database,Create().Database);
        foreach(var name in new[]{"migration.intent","notifications.migrating.db","storage-owner.txt"})
            Assert.False(File.Exists(Path.Combine(storage.DirectoryPath,name)));
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
