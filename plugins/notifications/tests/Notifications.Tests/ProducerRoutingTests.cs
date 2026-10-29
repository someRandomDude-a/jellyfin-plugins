using AccountHub.Api;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;

namespace Notifications.Tests;

public sealed class ProducerRoutingTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void StandaloneDiscoveryDoesNotMistakeTheLegacyHubAdapterForTheOwner(bool owner, bool api, bool expected)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(Contract.ServiceKey, (_, _) => (_, _) => Task.FromResult("{}"));
        services.AddKeyedSingleton<Func<bool>>(NotificationIntegration.OwnerKey, (_, _) => () => owner);
        if (api) services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(NotificationIntegration.ServiceKey, (_, _) => (_, _) => Task.FromResult("{}"));
        using var provider = services.BuildServiceProvider();
        Assert.True(new AccountHubClient(provider).IsAvailable);
        Assert.Equal(expected, NotificationIntegration.IsStandaloneInstalled(provider));
    }
    [Fact] public void UnreadableOwnerDiscoveryIsUnavailableRatherThanAnImplicitGrant()
    {
        using var provider = new ServiceCollection().AddKeyedSingleton<Func<bool>>(NotificationIntegration.OwnerKey, (_, _) => () => throw new IOException()).BuildServiceProvider();
        Assert.False(NotificationIntegration.IsStandaloneInstalled(provider));
    }
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task RequiredConsumersRejectMissingOrOlderUnscopedProviders(bool owner, bool scoped, bool expected)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<Func<bool>>("Notifications.Owner.1", (_, _) => () => owner);
        services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>("Notifications.Api.1", (_, _) => (json, _) => {
            using var request = JsonDocument.Parse(json);
            object data = request.RootElement.GetProperty("operation").GetString() == "hello"
                ? new { apiVersion = "1", operations = new[] { "notifications.publish" } }
                : new { ready = true, features = scoped ? new[] { "registered-types", "scoped-access", "commands" } : [] };
            return Task.FromResult(Contract.Serialize(Contract.Success(data)));
        });
        using var provider = services.BuildServiceProvider();
        Assert.Equal(expected, await new NotificationsClient(provider).ReadyForRegisteredTypesAsync());
    }
    [Fact] public async Task InternalClientRegistersTypesAndBuildsAdministratorAlertsWithoutCallerChosenRecipients()
    {
        var requests = new List<JsonElement>(); var services = new ServiceCollection();
        services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>("Notifications.Api.1", (_, _) => (json, _) => {
            using var document = JsonDocument.Parse(json); requests.Add(document.RootElement.Clone());
            return Task.FromResult(Contract.Serialize(Contract.Success(new { status = "accepted" })));
        });
        using var provider = services.BuildServiceProvider(); var client = new NotificationsClient(provider);
        await client.RegisterAsync(new("example-plugin", "1", [new("system.alert", "Example alert", "A service fails", "administrators", new(), [])]));
        await client.AlertAdministratorsAsync("example-plugin", "system.alert", "Service needs attention", "Check the saved connection.", dedupeKey: "connection:1");
        Assert.Equal("notifications.register", requests[0].GetProperty("operation").GetString());
        var entry = requests[1].GetProperty("payload"); Assert.True(entry.GetProperty("adminOnly").GetBoolean()); Assert.Equal(0, entry.GetProperty("audience").GetArrayLength());
        Assert.Equal("connection:1", entry.GetProperty("dedupeKey").GetString());
    }
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
