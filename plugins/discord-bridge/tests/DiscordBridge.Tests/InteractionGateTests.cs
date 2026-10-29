using Jellyfin.Plugin.DiscordBridge;
using Xunit;

namespace DiscordBridge.Tests;

public sealed class InteractionGateTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"DiscordBridge.Gate.Tests",Guid.NewGuid().ToString("N"));
    private BridgeStore Store(bool enabled=true){var store=new BridgeStore(directory);var current=store.Read();current.Settings.Enabled=enabled;current.Settings.GuildId="123456789";store.Save(current.Settings,current.Revision,null,false);return store;}

    [Fact]public async Task DuplicateGatewayEventCannotRaceTheOriginalReplyOrDispatchTwice(){
        var store=Store();var gate=new InteractionGate(store);var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int dispatched=0,denied=0;
        Task Deny(InteractionDenial _){denied++;return Task.CompletedTask;}
        var first=gate.HandleAsync("1000000001","123456789",async()=>{dispatched++;entered.SetResult();await release.Task;},Deny);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await gate.HandleAsync("1000000001","123456789",()=>{dispatched++;return Task.CompletedTask;},Deny);
        Assert.Equal(1,dispatched);Assert.Equal(0,denied);release.SetResult();await first;
        await new InteractionGate(new BridgeStore(directory)).HandleAsync("1000000001","123456789",()=>throw new Exception("Completed work replayed"),Deny);
        Assert.Equal(0,denied);
    }
    [Theory][InlineData(null)][InlineData("987654321")]
    public async Task WrongServerOrDmHasExplicitGuidanceAndDoesNotConsumeAClaim(string? guild){
        var store=Store();string? message=null;await new InteractionGate(store).HandleAsync("1000000002",guild,()=>throw new Exception("Wrong scope dispatched"),denial=>{message=denial.Message;Assert.Equal("wrong_server",denial.Code);return Task.CompletedTask;});
        Assert.Contains("configured Discord server",message);Assert.Contains("not in bot DMs",message);Assert.True(store.Claim("1000000002"));
    }
    [Fact]public async Task FailedDispatchRetainsItsClaimAndNeverReplaysOnRestart(){
        var store=Store();await Assert.ThrowsAsync<IOException>(()=>new InteractionGate(store).HandleAsync("1000000003","123456789",()=>throw new IOException("Unknown outcome"),_=>Task.CompletedTask));
        await new InteractionGate(new BridgeStore(directory)).HandleAsync("1000000003","123456789",()=>throw new Exception("Uncertain work replayed"),_=>throw new Exception("Duplicate reply raced original"));
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
