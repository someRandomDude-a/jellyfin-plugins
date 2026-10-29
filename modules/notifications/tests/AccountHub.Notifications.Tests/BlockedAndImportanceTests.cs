using AccountHub.Api;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class BlockedAndImportanceTests
{
    [Fact]public async Task MutedNoticesRemainBoundedAndRestoreWithoutExternalReplay(){
        var f=new Fixture();f.Store.SaveSettings(new(0,[]){BlockedNotificationLimit=2});f.Store.Preferences(f.Host.Alice,[new("*","*","in-app",false)]);
        f.Store.ConfigurePush(0,true,"mailto:test@example.test");using var key=System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);var point=key.ExportParameters(false);
        f.Store.SavePush(f.Host.Alice,new("https://fcm.googleapis.com/disposable",WebPush.Encode([4,..point.Q.X!,..point.Q.Y!]),WebPush.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16))));
        for(var i=0;i<3;i++)Assert.Empty((await f.Publish(f.Event() with{Title="Muted "+i})).NotificationIds);
        Assert.Empty(f.Store.Inbox(f.Host.Alice,0,25,null,false));Assert.Equal(0,f.Store.Unread(f.Host.Alice));
        var blocked=f.Store.Inbox(f.Host.Alice,0,25,null,false,blocked:true);Assert.Equal(2,blocked.Count);Assert.Equal("Muted 2",blocked[0].Event.Title);Assert.Empty(f.Store.Inbox(f.Host.Bob,0,25,null,false,blocked:true));
        Assert.Null(f.Store.InboxEvent(f.Host.Alice,blocked[0].Id,true));await f.Service.RestoreBlockedAsync(f.Host.Alice,default);Assert.Equal(2,f.Store.Inbox(f.Host.Alice,0,25,null,false,blocked:true).Count);
        f.Store.Preferences(f.Host.Alice,[new("*","*","in-app",true)]);await f.Service.RestoreBlockedAsync(f.Host.Alice,default);
        Assert.Empty(f.Store.Inbox(f.Host.Alice,0,25,null,false,blocked:true));Assert.All(f.Store.Inbox(f.Host.Alice,0,25,null,false),row=>Assert.True(row.Event.Retroactive));Assert.Empty(f.Store.PushJobs());Assert.Null(f.Store.Claim());
    }
    [Fact]public async Task RetentionAndCurrentMediaPermissionsStillGateBlockedHistory(){
        var f=new Fixture();f.Store.Preferences(f.Host.Alice,[new("*","*","in-app",false)]);await f.Publish(f.Event() with{Type="library.movie.added"});
        f.Host.LibraryDenied.Add(f.Host.Alice);f.Store.Preferences(f.Host.Alice,[new("*","*","in-app",true)]);await f.Service.RestoreBlockedAsync(f.Host.Alice,default);Assert.Empty(f.Store.Inbox(f.Host.Alice,0,25,null,false));
        f.Clock.Now=f.Clock.Now.AddDays(91);Assert.Empty(f.Store.Inbox(f.Host.Alice,0,25,null,false,blocked:true));
    }
    [Fact]public async Task NoncriticalAdministratorTypesAreConfigurableButCriticalAlertsRemainHighAndVisible(){
        var f=new Fixture();f.Store.Preferences(f.Host.Bob,[new("*","*","in-app",false){Importance="silent"},new("deletion-queue","proposal.updated","in-app",false){Importance="high"}]);
        await f.Publish(f.Event(true));Assert.Single(f.Store.Inbox(f.Host.Bob,0,25,null,false,blocked:true));Assert.Empty(f.Store.Inbox(f.Host.Bob,0,25,null,false));
        var important=await f.Publish(f.Event(true) with{Severity="critical"});Assert.Single(important.NotificationIds);var entry=Assert.Single(f.Store.Inbox(f.Host.Bob,0,25,null,false)).Event;
        Assert.Equal("high",(await f.Service.PresentAsync(f.Host.Bob,entry,[],default)).Importance);
    }
    [Fact]public async Task ImportanceInheritsByPluginThenCategoryWithoutRewritingProducerSeverity(){
        var f=new Fixture();f.Store.Preferences(f.Host.Bob,[new("*","*","in-app",true){Importance="low"},new("deletion-queue","*","in-app",true){Importance="silent"},new("deletion-queue","proposal.updated","in-app",true){Importance="high"}]);
        var e=f.Event(true);var result=await f.Service.PresentAsync(f.Host.Bob,e,[],default);Assert.Equal("high",result.Importance);Assert.Equal("info",result.Severity);
        Assert.Equal("silent",(await f.Service.PresentAsync(f.Host.Bob,e with{Type="proposal.created"},[],default)).Importance);
        await Assert.ThrowsAsync<NotificationException>(()=>f.Publish(e with{Importance="high"}));
    }
    [Fact] public async Task OrdinaryUsersChooseImportanceForTheirOwnInboxAndCannotOverrideCriticalSeverity() {
        var f = new Fixture(); f.Store.Preferences(f.Host.Alice, [new("deletion-queue", "*", "in-app", true) { Importance = "silent" }]);
        Assert.Equal("silent", (await f.Service.PresentAsync(f.Host.Alice, f.Event(), [], default)).Importance);
        Assert.Equal("normal", (await f.Service.PresentAsync(f.Host.Bob, f.Event(), [], default)).Importance);
        Assert.Equal("high", (await f.Service.PresentAsync(f.Host.Alice, f.Event() with { Severity = "critical" }, [], default)).Importance);
    }
    [Fact] public async Task WatchlistedAndFavoritedFiltersStaySeparateAndRequireCurrentMediaAccess() {
        var f = new Fixture();
        f.Store.Preferences(f.Host.Alice, [new("account-hub", "category.library.all", "in-app", false), new("account-hub", "category.library.watchlisted", "in-app", true) { Importance = "high" }]);
        NotificationEvent Media(string categories) => f.Event() with { Source = "account-hub", Type = "library.episode.added", Metadata = new Dictionary<string, string> { ["libraryCategories"] = categories } };
        Assert.Single((await f.Publish(Media("all,watchlisted"))).NotificationIds);
        Assert.Empty((await f.Publish(Media("all,favourite"))).NotificationIds);
        Assert.Equal("high", (await f.Service.PresentAsync(f.Host.Alice, Media("all,watchlisted"), [], default)).Importance);
        f.Host.LibraryDenied.Add(f.Host.Alice);
        Assert.Empty((await f.Publish(Media("all,watchlisted,favourite"))).NotificationIds);
        f.Store.Preferences(f.Host.Alice, [new("account-hub", "category.library.all", "in-app", false), new("account-hub", "category.library.favourite", "in-app", true) { Importance = "high" }]);
        f.Host.LibraryDenied.Clear(); Assert.Single((await f.Publish(Media("all,favourite"))).NotificationIds);
        Assert.Empty((await f.Publish(Media("all,watchlisted"))).NotificationIds);
    }
    [Fact] public void OverlappingInterestsChooseTheHighestImportanceButExplicitEventOverridesWin() {
        var e = new Fixture().Event() with { Source = "account-hub", Type = "library.episode.added", Metadata = new Dictionary<string, string> { ["libraryCategories"] = "all,favourite,watchlisted" } };
        Preference[] preferences = [new("account-hub", "category.library.watchlisted", "in-app", true) { Importance = "silent" }, new("account-hub", "category.library.favourite", "in-app", true) { Importance = "high" }];
        Assert.Equal("high", NotificationService.Importance(preferences, e));
        Assert.Equal("silent", NotificationService.Importance([..preferences, new("account-hub", e.Type, "in-app", true) { Importance = "silent" }], e));
        Assert.Equal("normal", NotificationService.Importance([preferences[1] with { Enabled = false }], e));
    }
    [Fact] public async Task DisablingNotificationsSilencesExistingRowsWithoutRewritingHistoryOrCriticalSeverity() {
        var f = new Fixture(); var e = f.Event(); await f.Publish(e);
        f.Store.Preferences(f.Host.Alice, [new("deletion-queue", "*", "in-app", false) { Importance = "high" }]);
        var presented = await f.Service.PresentAsync(f.Host.Alice, e, [], default);
        Assert.Equal("silent", presented.Importance); Assert.Equal("info", presented.Severity);
        Assert.Equal(e.EventId, Assert.Single(f.Store.Inbox(f.Host.Alice, 0, 25, null, false)).Event.EventId);
        Assert.Equal("high", (await f.Service.PresentAsync(f.Host.Alice, e with { Severity = "critical" }, [], default)).Importance);
    }
}
