using AccountHub.Api;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class GroupingTests
{
    private static NotificationRegistration Registration(string source="deletion-queue") => new(source,"1",[new("proposal.updated","Proposal","Proposal changes","explicit-users",new(),[])]) { Grouping=new("showId","Show","showName") };
    [Fact] public async Task ProducerKeyAndLabelAreProjectedPerRecipientWithoutChangingStoredEvents()
    {
        var f=new Fixture();f.Service.Register(Registration());
        var entry=f.Event() with {Metadata=new(){["showId"]="show-42",["showName"]="Example show"}};
        await f.Publish(entry);
        var first=await f.Service.PresentAsync(f.Host.Alice,entry,f.Service.Catalog(),default);
        var second=await f.Service.PresentAsync(f.Host.Alice,entry with {EventId=Guid.NewGuid(),Title="Another episode"},f.Service.Catalog(),default);
        Assert.Equal(first.Group,second.Group);Assert.Equal("Example show",first.Group!.Label);Assert.Equal(64,first.Group.Key.Length);Assert.DoesNotContain("show-42",first.Group.Key);
        Assert.Null(f.Store.Inbox(f.Host.Alice,0,10,null,false,false).Single().Event.Group);
        Assert.Null((await f.Service.PresentAsync(f.Host.Alice,entry with {Metadata=null},f.Service.Catalog(),default)).Group);
        Assert.Null((await f.Service.PresentAsync(f.Host.Alice,entry with {Severity="critical"},f.Service.Catalog(),default)).Group);
    }
    [Fact] public async Task PersonalOverrideWinsOverRevisionedPluginDefaultAndCanBeClearedToInherit()
    {
        var f=new Fixture();f.Service.Register(Registration());var entry=f.Event() with{Metadata=new(){["showId"]="42"}};
        f.Service.SaveGrouping(new("deletion-queue",1,false));Assert.Equal(2,f.Service.Catalog().Single().Revision);
        Assert.Throws<NotificationException>(()=>f.Service.SaveGrouping(new("deletion-queue",1,true)));
        Assert.Null((await f.Service.PresentAsync(f.Host.Alice,entry,f.Service.Catalog(),default)).Group);
        f.Store.SetGroupingPreference(f.Host.Alice,new("deletion-queue",true));
        Assert.NotNull((await f.Service.PresentAsync(f.Host.Alice,entry,f.Service.Catalog(),default)).Group);
        Assert.Null((await f.Service.PresentAsync(f.Host.Bob,entry,f.Service.Catalog(),default)).Group);
        f.Store.SetGroupingPreference(f.Host.Alice,new("deletion-queue",null));Assert.Empty(f.Store.GroupingPreferences(f.Host.Alice));
        Assert.Null((await f.Service.PresentAsync(f.Host.Alice,entry,f.Service.Catalog(),default)).Group);
    }
    [Fact] public async Task GroupsAreSourceScopedAndPreferencesAndDefaultsSurviveRestartAndReregistration()
    {
        var f=new Fixture();var registration=Registration();f.Service.Register(registration);f.Service.Register(Registration("discord-bridge"));
        var entry=f.Event() with{Metadata=new(){["showId"]="42"}};
        var a=await f.Service.PresentAsync(f.Host.Alice,entry,f.Service.Catalog(),default);
        var b=await f.Service.PresentAsync(f.Host.Alice,entry with{Source="discord-bridge"},f.Service.Catalog(),default);
        Assert.NotEqual(a.Group!.Key,b.Group!.Key);
        f.Service.SaveGrouping(new("deletion-queue",1,false));f.Store.SetGroupingPreference(f.Host.Alice,new("discord-bridge",false));
        f.Service.Register(registration with {Version="2"});
        var reopened=new NotificationStore(f.Options,f.Protection,f.Clock);
        Assert.False(reopened.NotificationTypes().Single(value=>value.Source=="deletion-queue").Grouping!.DefaultEnabled);
        Assert.False(reopened.GroupingPreferences(f.Host.Alice).Single().Enabled);
    }
    [Fact] public async Task ProducersCannotForgeGroupingProjectionOrDeclareSecretMetadataAsAGroupingKey()
    {
        var f=new Fixture();
        foreach(var key in new[]{"password","token","authorization","path","bad.key"})Assert.Throws<NotificationException>(()=>f.Service.Register(Registration() with{Grouping=new(key,"Unsafe")}));
        f.Service.Register(Registration());
        await Assert.ThrowsAsync<NotificationException>(()=>f.Publish(f.Event() with{Group=new("forged","Show")}));
        f.Service.Register(Registration("discord-bridge") with{Grouping=null});
        Assert.Throws<NotificationException>(()=>f.Service.SaveGrouping(new("discord-bridge",1,true)));
    }
}
