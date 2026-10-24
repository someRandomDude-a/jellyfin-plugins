using AccountHub.Api;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace Notifications.Tests;

public sealed class ProducerRoutingTests
{
    [Fact] public async Task NotificationReadinessAndPublicationDoNotDependOnHubHealth()
    {
        var calls=new List<string>();var services=new ServiceCollection();
        services.AddKeyedSingleton<Func<string,CancellationToken,Task<string>>>(Contract.ServiceKey,(_,_) => (_,_)=>Task.FromException<string>(new IOException("Hub unavailable")));
        services.AddKeyedSingleton<Func<string,CancellationToken,Task<string>>>("Notifications.Api.1",(_,_) => (json,_)=>{
            var operation=JsonDocument.Parse(json).RootElement.GetProperty("operation").GetString()!;calls.Add(operation);
            object data=operation switch {
                "hello"=>new { apiVersion="1",operations=new[]{"notifications.publish"} },
                "health"=>new { ready=true },
                _=>new { status="accepted" }
            };
            return Task.FromResult(Contract.Serialize(Contract.Success(data)));
        });
        using var provider=services.BuildServiceProvider();var client=new AccountHubClient(provider);
        Assert.True(await client.ReadyAsync(CancellationToken.None));
        Assert.NotNull(await client.CallAsync("notifications.publish",new{},CancellationToken.None));
        Assert.Equal(new[]{"hello","health","notifications.publish"},calls);
        Assert.Null(await client.CallAsync("audit.publish",new{},CancellationToken.None));
    }
}
