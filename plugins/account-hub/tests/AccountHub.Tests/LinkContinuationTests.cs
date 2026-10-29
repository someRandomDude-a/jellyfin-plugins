using AccountHub.Core;
using Xunit;

namespace AccountHub.Tests;

public sealed class LinkContinuationTests
{
    private const string Token="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    [Fact] public void NoncesAreOpaqueVolatileAndOneUse()
    {
        var clock=new TestClock();var store=new LinkContinuationStore(clock);var nonce=store.Prepare(Token,clock.Now.AddMinutes(5));
        Assert.Matches("^[A-Za-z0-9_-]{43}$",nonce);Assert.NotEqual(Token,nonce);Assert.Null(new LinkContinuationStore(clock).Take(nonce));
        Assert.Equal(Token,store.Take(nonce));Assert.Null(store.Take(nonce));Assert.Null(store.Take(Token));Assert.Null(store.Take(null));
    }
    [Theory,InlineData(30),InlineData(600)] public void ExpiryNeverExceedsChallengeOrFiveMinutes(int seconds)
    {
        var clock=new TestClock();var store=new LinkContinuationStore(clock);var nonce=store.Prepare(Token,clock.Now.AddSeconds(seconds));
        clock.Now=clock.Now.AddSeconds(Math.Min(seconds,300));Assert.Null(store.Take(nonce));
    }
    [Fact] public void ReplacementRevokesOnlyThePreviousTransportAndCapacityRecoversAfterExpiry()
    {
        var clock=new TestClock();var store=new LinkContinuationStore(clock);var original=store.Prepare(Token,clock.Now.AddMinutes(5));
        for(var i=1;i<LinkContinuationStore.Capacity;i++)store.Prepare(Token,clock.Now.AddMinutes(5));
        Assert.Throws<HubException>(()=>store.Prepare(Token,clock.Now.AddMinutes(5)));
        var replacement=store.Prepare(Token,clock.Now.AddMinutes(5),original);Assert.Null(store.Take(original));Assert.Equal(Token,store.Take(replacement));
        clock.Now=clock.Now.AddMinutes(5);Assert.NotNull(store.Prepare(Token,clock.Now.AddMinutes(5)));
    }
    [Fact] public async Task ConcurrentTakeHasExactlyOneWinnerAndNeverConsumesTheDiscordChallenge()
    {
        await using var fixture=new StoreFixture();await fixture.InitializeAsync();var links=new DiscordLinkService(fixture.Store,fixture.Clock);
        var token=await links.PrepareAsync("123456789012345678",default);var store=new LinkContinuationStore(fixture.Clock);var nonce=store.Prepare(token,fixture.Clock.Now.AddMinutes(5));
        var results=await Task.WhenAll(Enumerable.Range(0,16).Select(_=>Task.Run(()=>store.Take(nonce))));Assert.Single(results,value=>value is not null);
        Assert.NotNull(await links.LookupAsync(token,default));
    }
    [Theory,InlineData(""),InlineData("bad"),InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!")] public void MalformedInputsAreRejected(string value)
    {
        var clock=new TestClock();var store=new LinkContinuationStore(clock);Assert.Throws<HubException>(()=>store.Prepare(value,clock.Now.AddMinutes(5)));Assert.Null(store.Take(value));
        Assert.Throws<HubException>(()=>store.Prepare(Token,clock.Now));
    }
}
