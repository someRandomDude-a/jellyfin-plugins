using AccountHub.Api;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class ViewedExpiryTests
{
    [Fact]public async Task ExpiryStartsAtFirstViewDoesNotRenewAndArchivesOnlyThatRecipient(){
        var f=new Fixture();var e=f.Event() with{Audience=[f.Host.Alice,f.Host.Bob],Layout=new(){ExpireAfterViewHours=1}};await f.Publish(e);
        f.Clock.Now=f.Clock.Now.AddDays(10);
        var alice=Assert.Single(f.Store.Inbox(f.Host.Alice,0,25,null,false));var bob=Assert.Single(f.Store.Inbox(f.Host.Bob,0,25,null,false));Assert.Null(alice.Event.FirstViewedAt);
        var first=f.Clock.Now;Assert.True(f.Store.Mark(f.Host.Alice,alice.Id,false));
        f.Clock.Now=f.Clock.Now.AddMinutes(30);Assert.True(f.Store.Mark(f.Host.Alice,alice.Id,false));
        Assert.Equal(first,Assert.Single(f.Store.Inbox(f.Host.Alice,0,25,null,false)).Event.FirstViewedAt);
        f.Clock.Now=f.Clock.Now.AddMinutes(31);Assert.Empty(f.Store.Inbox(f.Host.Alice,0,25,null,false));
        var archived=Assert.Single(f.Store.Inbox(f.Host.Alice,0,25,null,false,history:true));Assert.True(archived.Read);Assert.True(archived.Dismissed);
        Assert.Null(f.Store.InboxEvent(f.Host.Alice,alice.Id,true));Assert.Single(f.Store.Inbox(f.Host.Bob,0,25,null,false));Assert.Null(bob.Event.FirstViewedAt);
        Assert.False(f.Store.Mark(f.Host.Bob,alice.Id,false));
    }
    [Fact]public async Task ProducersCannotForgeViewedTimesAndDisabledExpiryKeepsViewedRows(){
        var f=new Fixture();await Assert.ThrowsAsync<NotificationException>(()=>f.Publish(f.Event() with{FirstViewedAt=f.Clock.Now.AddDays(-10)}));
        foreach(var invalid in new[]{0,-1,8761})await Assert.ThrowsAsync<NotificationException>(()=>f.Publish(f.Event() with{Layout=new(){ExpireAfterViewHours=invalid}}));
        await f.Publish(f.Event());var row=Assert.Single(f.Store.Inbox(f.Host.Alice,0,25,null,false));f.Store.Mark(f.Host.Alice,row.Id,false);
        f.Clock.Now=f.Clock.Now.AddDays(10);Assert.Single(f.Store.Inbox(f.Host.Alice,0,25,null,false));
    }
}
