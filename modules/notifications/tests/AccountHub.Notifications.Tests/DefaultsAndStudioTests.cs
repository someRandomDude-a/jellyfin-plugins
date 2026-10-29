using AccountHub.Api;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class DefaultsAndStudioTests
{
    [Fact] public void StudioAssetsAreActuallyEmbeddedInTheShippedBackendAssembly()
    {
        foreach(var file in new[]{"notification-card.js","notification-studio.js","notification-studio.css"}) {
            using var stream=typeof(NotificationService).Assembly.GetManifestResourceStream("AccountHub.Notifications.Web."+file);
            Assert.NotNull(stream);Assert.True(stream.Length>0);
        }
    }
    private static NotificationRegistration Registration() => new("deletion-queue", "1", [
        new("proposal.updated", "Proposal", "A proposal changes", "explicit-users", new(), []) {
            Defaults = new(false, false, "low"), Example = new("Example proposal", "Nothing is sent.")
        }
    ]);
    [Fact] public async Task AdministratorDefaultsAreRevisionedAndPersonalChoicesAndCriticalSeverityWin()
    {
        var f = new Fixture(); f.Service.Register(Registration());
        Assert.Empty((await f.Publish(f.Event())).NotificationIds);
        f.Service.SaveDefaults(new("deletion-queue", "proposal.updated", 1, new(true, false, "high")));
        Assert.Equal("revision_changed", Assert.Throws<NotificationException>(() => f.Service.SaveDefaults(new("deletion-queue", "proposal.updated", 1, new()))).Code);
        var e = f.Event(); Assert.Single((await f.Publish(e)).NotificationIds);
        Assert.Equal("high", (await f.Service.PresentAsync(f.Host.Alice, e, f.Service.Catalog(), default)).Importance);
        f.Store.Preferences(f.Host.Alice, [new("deletion-queue", "proposal.updated", "in-app", true) { Importance = "silent" }]);
        Assert.Equal("silent", (await f.Service.PresentAsync(f.Host.Alice, e, f.Service.Catalog(), default)).Importance);
        f.Service.SaveDefaults(new("deletion-queue", "proposal.updated", 2, new(false, false, "silent")));
        Assert.Single((await f.Publish(f.Event())).NotificationIds);
        f.Store.Preferences(f.Host.Alice, [new("*", "*", "in-app", false) { Importance = "silent" }]);
        var critical = f.Event() with { Severity = "critical" };
        Assert.Single((await f.Publish(critical)).NotificationIds);
        Assert.Equal("high", (await f.Service.PresentAsync(f.Host.Alice, critical, f.Service.Catalog(), default)).Importance);
    }
    [Fact] public void DefaultsAndCanvasSurviveProducerUpdatesAndTemporaryTypeRemoval()
    {
        var f = new Fixture(); var registration = Registration(); f.Service.Register(registration);
        var canvas = new NotificationCanvas(180, [new("title", "title", 0, 0, 100, 50, FontFamily: "serif", FontWeight: 700)]);
        f.Service.SaveLayout(new("deletion-queue", "proposal.updated", 1, new() { Canvas = canvas }));
        f.Service.SaveDefaults(new("deletion-queue", "proposal.updated", 2, new(Importance: "high")));
        f.Service.Register(registration with { Version = "2", Types = [registration.Types[0] with { Type = "proposal.created" }] });
        Assert.Null(f.Service.Catalog().Single().Types.Single().Layout.Canvas);
        f.Service.Register(registration);
        var store = new NotificationStore(f.Options, f.Protection, f.Clock);
        var type = store.NotificationTypes().Single().Types.Single();
        Assert.Equal("high", type.Defaults!.Importance); Assert.Equal("serif", type.Layout.Canvas!.Layers!.Single().FontFamily);
    }
    [Fact] public void InvalidCanvasGeometryFontsAndUnboundedLayersAreRejectedWithoutChangingRevision()
    {
        var f = new Fixture(); f.Service.Register(Registration());
        var layer = new NotificationLayer("title", "title", 0, 0, 100, 50);
        foreach (var invalid in new[] { layer with { X = double.NaN }, layer with { X = 70 }, layer with { FontFamily = "url(https://example.test)" }, layer with { Kind = "html" }, layer with { Text = new string('x',401) }, layer with { FontWeight = 999 } })
            Assert.Throws<NotificationException>(() => f.Service.SaveLayout(new("deletion-queue", "proposal.updated", 1, new() { Canvas = new(180, [invalid]) })));
        foreach (var canvas in new[] { new NotificationCanvas(99, [layer]), new NotificationCanvas(180, []), new NotificationCanvas(180, [layer,layer]) })
            Assert.Throws<NotificationException>(() => f.Service.SaveLayout(new("deletion-queue", "proposal.updated", 1, new() { Canvas = canvas })));
        Assert.Throws<NotificationException>(() => f.Service.SaveDefaults(new("deletion-queue", "proposal.updated", 1, new(Importance: "critical"))));
        Assert.Equal(1, f.Service.Catalog().Single().Revision);
    }
    [Fact] public async Task LegacyLibraryChoicesAreAliasedButNewOwnerRulesTakePrecedence()
    {
        var f = new Fixture(); var type = "library.movie.added";
        f.Store.Preferences(f.Host.Alice, [new("account-hub", type, "in-app", false), new("notifications", type, "in-app", true) { Importance = "low" }]);
        var entry = f.Event() with { Source = "notifications", Type = type };
        Assert.Equal("low",(await f.Service.PresentAsync(f.Host.Alice,entry,[],default)).Importance);
        f.Store.SetPreference(f.Host.Alice, new("notifications", type, "in-app", false));
        Assert.All(f.Store.Preferences(f.Host.Alice), rule => Assert.False(rule.Enabled));
        Assert.Equal("silent",(await f.Service.PresentAsync(f.Host.Alice,entry with{Source="account-hub"},[],default)).Importance);
    }
    [Fact] public async Task LegacyNativeLibraryDrainDoesNotDependOnAnAbsentHubAccessProvider()
    {
        var f=new Fixture();f.Service.Register(new("account-hub","old",[new("account.*","Accounts","Account changes","explicit-users",new(),[])]){RequireAccess=true});
        f.Host.MissingAccessProviders.Add("account-hub");
        f.Service.Register(new("notifications","1",[new("library.movie.added","Movies","An addition","explicit-users",new(),[]){Defaults=new(Importance:"low")} ]));
        var e=f.Event() with{Source="account-hub",Type="library.movie.added",Metadata=new Dictionary<string,string>{["itemId"]=Guid.NewGuid().ToString("D")}};
        await Assert.ThrowsAsync<NotificationException>(()=>f.Publish(e));
        Assert.Single((await f.Service.PublishLibraryAsync(e,default)).NotificationIds);
        Assert.Equal("low",(await f.Service.PresentAsync(f.Host.Alice,e,f.Service.Catalog(),default)).Importance);
        Assert.Equal("duplicate",(await f.Service.PublishLibraryAsync(e,default)).Status);
        f.Host.LibraryDenied.Add(f.Host.Alice);
        Assert.Empty((await f.Service.PublishLibraryAsync(e with{EventId=Guid.NewGuid()},default)).NotificationIds);
    }
}
