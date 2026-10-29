using AccountHub.Core;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AccountHub.Tests;

public sealed class InviteDefaultsTests
{
    [Fact]public async Task MultiUseOptionsAreOwnerBoundAndIncludedInTheDurableCommandFingerprint(){
        await using var f=new StoreFixture();await f.InitializeAsync();var service=new InviteDefaultsService(f.Store,new CommandGate(f.Store,f.Clock),f.Clock);var owner=Guid.NewGuid();var group=Guid.NewGuid();
        await service.SaveAsync(owner,Guid.NewGuid(),0,new(group,true,[owner]){DiscordDefaultUses=2,DiscordMaxUses=4,DiscordMaxExpiryMinutes=120},_=>Task.CompletedTask);
        foreach(var options in new[]{(5,60),(0,60),(2,121),(2,14)})await Assert.ThrowsAsync<HubException>(()=>service.IssueDiscordAsync(owner,Guid.NewGuid(),1,group,"{}",_=>Task.CompletedTask,uses:options.Item1,expiryMinutes:options.Item2));
        var command=Guid.NewGuid();await service.IssueDiscordAsync(owner,command,1,group,"{}",_=>Task.CompletedTask,uses:3,expiryMinutes:90);
        var invite=await f.Store.ReadAsync((db,ct)=>db.OnboardingInvites.AsNoTracking().SingleAsync(ct));Assert.Equal(3,invite.MaxUses);Assert.Equal(90*60000L,invite.ExpiresAt-invite.CreatedAt);
        await Assert.ThrowsAsync<HubException>(()=>service.IssueDiscordAsync(owner,command,1,group,"{}",_=>Task.CompletedTask,uses:4,expiryMinutes:90));
        Assert.Equal(1,await f.Store.ReadAsync((db,ct)=>db.OnboardingInvites.CountAsync(ct)));
    }
    [Fact]public async Task DelegationIsDefaultDeniedAndOnlyAllowedCreatorCanIssueTheConfiguredTemplate(){
        await using var f=new StoreFixture();await f.InitializeAsync();var service=new InviteDefaultsService(f.Store,new CommandGate(f.Store,f.Clock),f.Clock);
        Assert.False((await service.ReadAsync()).DiscordCreationEnabled);var owner=Guid.NewGuid();var template=Guid.NewGuid();
        await service.SaveAsync(owner,Guid.NewGuid(),0,new(template,true,[owner]),_=>Task.CompletedTask);
        await Assert.ThrowsAsync<HubException>(()=>service.IssueDiscordAsync(Guid.NewGuid(),Guid.NewGuid(),1,template,"{}",_=>Task.CompletedTask));
        await Assert.ThrowsAsync<HubException>(()=>service.IssueDiscordAsync(owner,Guid.NewGuid(),1,Guid.NewGuid(),"{}",_=>Task.CompletedTask));
        Assert.Equal(0,await f.Store.ReadAsync((db,ct)=>db.OnboardingInvites.CountAsync(ct)));
    }
    [Fact]public async Task IssuanceIsSingleUseBoundedAuditedAndNeverReplaysTheCredential(){
        await using var f=new StoreFixture();await f.InitializeAsync();var service=new InviteDefaultsService(f.Store,new CommandGate(f.Store,f.Clock),f.Clock);var owner=Guid.NewGuid();var group=Guid.NewGuid();
        await service.SaveAsync(owner,Guid.NewGuid(),0,new(group,true,[owner],60,1),_=>Task.CompletedTask);
        var command=Guid.NewGuid();var first=await service.IssueDiscordAsync(owner,command,1,group,"{}",_=>Task.CompletedTask);Assert.NotNull(first.Token);
        var replay=await service.IssueDiscordAsync(owner,command,1,group,"{}",_=>throw new Exception("Completed retry must not reauthorize"));Assert.Null(replay.Token);
        var invite=await f.Store.ReadAsync((db,ct)=>db.OnboardingInvites.AsNoTracking().SingleAsync(ct));Assert.Equal(1,invite.MaxUses);Assert.Equal("active",invite.Status);Assert.Equal(InviteOnboardingService.Hash(first.Token!),invite.TokenHash);
        Assert.DoesNotContain(first.Token!,await f.Store.ReadAsync((db,ct)=>db.Commands.Where(c=>c.Operation=="invite.discord.issued").Select(c=>c.ResultJson).SingleAsync(ct)));
        await Assert.ThrowsAsync<HubException>(()=>service.IssueDiscordAsync(owner,Guid.NewGuid(),2,group,"{}",_=>Task.CompletedTask));
        Assert.True((await f.Store.VerifyAuditAsync()).Valid);
    }
    [Fact]public async Task RevokingDelegationBlocksNewIssuanceWithoutResettingExistingInvites(){
        await using var f=new StoreFixture();await f.InitializeAsync();var service=new InviteDefaultsService(f.Store,new CommandGate(f.Store,f.Clock),f.Clock);var owner=Guid.NewGuid();var group=Guid.NewGuid();
        await service.SaveAsync(owner,Guid.NewGuid(),0,new(group,true,[owner]),_=>Task.CompletedTask);
        await service.IssueDiscordAsync(owner,Guid.NewGuid(),1,group,"{}",_=>Task.CompletedTask);
        await service.SaveAsync(owner,Guid.NewGuid(),2,new(group,false,[owner]),_=>Task.CompletedTask);
        await Assert.ThrowsAsync<HubException>(()=>service.IssueDiscordAsync(owner,Guid.NewGuid(),3,group,"{}",_=>Task.CompletedTask));
        Assert.Equal(1,await f.Store.ReadAsync((db,ct)=>db.OnboardingInvites.CountAsync(ct)));
        using var reopened=f.Reopen();await reopened.InitializeAsync();Assert.False((await new InviteDefaultsService(reopened,new CommandGate(reopened,f.Clock),f.Clock).ReadAsync()).DiscordCreationEnabled);
    }
}
