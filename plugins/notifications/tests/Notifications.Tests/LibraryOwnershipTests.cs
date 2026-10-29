using System.Text.Json;
using AccountHub.Api;
using AccountHub.Notifications;
using Jellyfin.LibraryNotifications;
using Jellyfin.Plugin.Notifications;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Notifications.Tests;

public sealed class LibraryOwnershipTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "opencode", "library-owner-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static LibraryNotice Notice() => new(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], "movie", "A movie", "Available now", Now, [Guid.NewGuid()]);
    [Fact] public void ImportPreservesIdsUncertainSendsAndOriginalTablesWithoutReplayingOnRestart()
    {
        var source = Path.Combine(root,"hub.db"); var old = new LibraryNoticeStore(source); var notice = Notice();
        old.SaveSettings(new(0,true)); old.Capture(notice.ItemId,notice.Kind,Now,notice); old.DiscordClaim(notice.Id);
        var current = new LibraryNoticeStore(Path.Combine(root,"notifications.db")); current.Import(source,true);
        var job = Assert.Single(current.Due(Now.AddMinutes(1))); Assert.Equal(notice.Id,job.Id); Assert.True(job.LegacyInbox);
        Assert.Equal(3,job.DiscordState); Assert.Equal("account-hub",job.Notice!.LegacySource); Assert.True(current.Settings().Enabled);
        current.InboxDone(job.Id); current.Import(source,true); Assert.Empty(current.Due(Now.AddMinutes(1)));
        Assert.Equal(1,JsonSerializer.SerializeToElement(current.Health()).GetProperty("uncertainDiscord").GetInt64());
        Assert.Single(old.Due(Now.AddMinutes(1))); // Source remains readable and unchanged.
        Assert.Empty(new LibraryNoticeStore(Path.Combine(root,"notifications.db")).Due(Now.AddMinutes(1)));
    }
    [Fact] public void BridgeTransportHistoryCannotAcknowledgePendingPrivateInboxWork()
    {
        var notice=Notice(); var hubPath=Path.Combine(root,"hub.db"); var bridgePath=Path.Combine(root,"bridge.db");
        var hub=new LibraryNoticeStore(hubPath); hub.Capture(notice.ItemId,notice.Kind,Now,notice);
        var bridge=new LibraryNoticeStore(bridgePath); var forwarded=notice with{Id=Guid.NewGuid(),Audience=[]};
        bridge.Capture(forwarded.ItemId,forwarded.Kind,Now,forwarded); bridge.InboxDone(forwarded.Id); bridge.DiscordDone(forwarded.Id);
        var owner=new LibraryNoticeStore(Path.Combine(root,"notifications.db")); owner.Import(hubPath,true); owner.Import(bridgePath,false);
        var job=Assert.Single(owner.Due(Now.AddMinutes(1))); Assert.Equal(notice.Id,job.Id); Assert.False(job.InboxDone); Assert.Equal(1,job.DiscordState);
    }
    [Fact] public void OversizedImportRollsBackInsteadOfDroppingOrSilentlyAcknowledgingHistory()
    {
        var source=Path.Combine(root,"hub.db"); _=new LibraryNoticeStore(source);
        using(var db=new SqliteConnection("Data Source="+source)) { db.Open(); using var command=db.CreateCommand();
            command.CommandText="WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10001) INSERT INTO library_notices SELECT printf('%032d',x),printf('%032d',x),'movie',0,NULL,1,3,0,0 FROM n"; command.ExecuteNonQuery(); }
        var owner=new LibraryNoticeStore(Path.Combine(root,"notifications.db")); Assert.Throws<InvalidDataException>(()=>owner.Import(source,true));
        Assert.Equal(0,JsonSerializer.SerializeToElement(owner.Health()).GetProperty("retained").GetInt64());
        Assert.Throws<InvalidDataException>(()=>owner.Import(source,true));
    }
    [Fact] public async Task StandaloneOwnerPublishesWithNativeRecipientChecksAndStripsPrivateBridgeAudiences()
    {
        var notice=Notice() with{EnrichmentVersion=2}; var user=notice.Audience[0]; notice=notice with{WatchlistedAudience=[user]};
        var options=new NotificationOptions{DatabasePath=Path.Combine(root,"notifications.db")};
        Directory.CreateDirectory(root); var store=new NotificationStore(options,DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root,"keys"))),TimeProvider.System);
        var host=new Mock<INotificationHost>(); host.SetupGet(h=>h.NotificationRegistrations).Returns(Array.Empty<NotificationRegistration>()); host.Setup(h=>h.UserExistsAsync(user,It.IsAny<CancellationToken>())).ReturnsAsync(true);
        host.Setup(h=>h.CanAccessNotificationAsync(user,It.IsAny<NotificationEvent>(),It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var services=new ServiceCollection(); services.AddSingleton(store); services.AddSingleton(new NotificationService(store,host.Object,options));
        services.AddSingleton<DiscordBridge.Api.DiscordBridgeClient>(); string? bridgeJson=null;
        services.AddKeyedSingleton<Func<string,CancellationToken,Task<string>>>(DiscordBridge.Api.DiscordBridgeClient.ServiceKey,(_,_) => (json,_)=>{bridgeJson=json;return Task.FromResult("{\"apiVersion\":\"1\",\"ok\":true}");});
        using var provider=services.BuildServiceProvider(); var library=new LibraryNoticeStore(options.DatabasePath); library.SaveSettings(new(0,true));
        notice=notice with{OccurredAt=DateTimeOffset.UtcNow.AddSeconds(1)}; var owner=new LibraryNotifications(provider,library);
        Assert.True(await owner.InboxAsync(notice,default)); var entry=Assert.Single(store.Inbox(user,0,25,null,false)).Event;
        Assert.Equal("notifications",entry.Source); Assert.Equal("all,watchlisted",entry.Metadata!["libraryCategories"]);
        Assert.True(await owner.InboxAsync(notice,default)); Assert.Single(store.Inbox(user,0,25,null,false));
        Assert.True(await owner.DiscordAsync(notice,default)); Assert.NotNull(bridgeJson);
        Assert.DoesNotContain(user.ToString("D"),bridgeJson); Assert.DoesNotContain("watchlistedAudience",bridgeJson);
        host.Setup(h=>h.CanAccessNotificationAsync(user,It.IsAny<NotificationEvent>(),It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await owner.InboxAsync(notice with{Id=Guid.NewGuid()},default); Assert.Single(store.Inbox(user,0,25,null,false));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if(Directory.Exists(root)) Directory.Delete(root,true); }
}
