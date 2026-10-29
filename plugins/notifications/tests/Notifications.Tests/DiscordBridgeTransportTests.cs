using DiscordBridge.Api;
using Jellyfin.Plugin.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Notifications.Tests;

public sealed class DiscordBridgeTransportTests
{
    [Theory][InlineData(true)][InlineData(false)]
    public async Task NativeTransportPreservesBridgeRetryPolicy(bool retryable){
        using var services=new ServiceCollection().AddKeyedSingleton<Func<string,CancellationToken,Task<string>>>(DiscordBridgeClient.ServiceKey,(_,_)=> (_,_)=>Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new{apiVersion="1",ok=false,error=new{code="denied",retryable}}))).BuildServiceProvider();
        var result=await new BridgeTransport(new DiscordBridgeClient(services)).SendAsync("123456789",true,"Synthetic",Guid.NewGuid().ToString(),default);
        Assert.False(result.Delivered);Assert.Equal(retryable,result.Retryable);
    }
}
