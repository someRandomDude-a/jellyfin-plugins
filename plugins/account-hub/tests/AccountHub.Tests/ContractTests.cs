using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using AccountHub.Api;
using AccountHub.Api.Contracts;
using AccountHub.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccountHub.Tests;

public sealed class ContractTests
{
    [Fact]
    public void NativeDomainAuditSummariesAreAcceptedWithoutAllowingUnknownNestedSecrets()
    {
        var value = FoundationTests.Event() with
        {
            Source = "private-syncplay",
            Metadata = JsonSerializer.SerializeToElement(new { partyId = Guid.NewGuid(), guestLabel = "Guest 1", reason = (string?)null,
                policy = new { scope = "users", userPolicies = new[] { new { userId = Guid.NewGuid(), before = new { guestInvites = "Allow" }, after = new { guestInvites = "Deny" } } } } })
        };
        EventValidation.Audit(value);
        EventValidation.Audit(value with { Source = "deletion-queue", Metadata = JsonSerializer.SerializeToElement(new {
            before = new { defaultAuthority = "OwnRequests", automaticDeletion = false }, after = new { defaultAuthority = "Denied", automaticDeletion = false } }) });
        Assert.Throws<HubException>(() => EventValidation.Audit(value with { Metadata = JsonSerializer.SerializeToElement(new { policy = new { password = "never store" } }) }));
    }
    [Fact]
    public async Task MissingHostIsUnavailableNotAnException()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var response = await new AccountHubApi(provider).CallAsync("hello", new { });
        Assert.False(response.Ok);
        Assert.Equal("unavailable", response.Error!.Code);
        Assert.True(response.Error.Retryable);
    }

    [Fact]
    public async Task ProducerAndHostInSeparateAlcsDiscoverFrameworkDelegate()
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        var hostContext = new IsolatedContext("hub-host", ["AccountHub.Core", "AccountHub.Api"]);
        var producerContext = new IsolatedContext("domain-producer", ["AccountHub.Api"]);
        try
        {
            var hostCore = hostContext.LoadFromAssemblyPath(typeof(HubStore).Assembly.Location);
            var hostStoreType = hostCore.GetType("AccountHub.Core.HubStore")!;
            var hostPathsType = hostCore.GetType("AccountHub.Core.HubPaths")!;
            // The host owns its independent instance and does not receive any Core-defined object from the producer.
            using var hostStore = (IDisposable)Activator.CreateInstance(hostStoreType,
                Activator.CreateInstance(hostPathsType, f.Paths.DataDirectory), f.Clock)!;
            await (Task)hostStoreType.GetMethod("InitializeAsync")!.Invoke(hostStore, [CancellationToken.None])!;
            var dispatcherType = hostCore.GetType("AccountHub.Core.ProducerDispatcher")!;
            var dispatcher = Activator.CreateInstance(dispatcherType, hostStore)!;
            var dispatch = dispatcherType.GetMethod("DispatchAsync")!
                .CreateDelegate<Func<string, CancellationToken, Task<string>>>(dispatcher);
            using var provider = new ServiceCollection().AddKeyedSingleton(Wire.ServiceKey, dispatch).BuildServiceProvider();
            var producerApi = producerContext.LoadFromAssemblyPath(typeof(AccountHubApi).Assembly.Location);
            Assert.NotSame(typeof(AccountHubApi).Assembly, producerApi);
            Assert.NotSame(producerApi, hostContext.Assemblies.Single(a => a.GetName().Name == "AccountHub.Api"));
            var clientType = producerApi.GetType("AccountHub.Api.AccountHubApi")!;
            var client = Activator.CreateInstance(clientType, provider)!;
            var method = clientType.GetMethod("CallAsync")!.MakeGenericMethod(typeof(object));
            var helloTask = (Task)method.Invoke(client, ["hello", new { }, CancellationToken.None])!;
            await helloTask;
            var response = helloTask.GetType().GetProperty("Result")!.GetValue(helloTask)!;
            var json = JsonSerializer.SerializeToElement(response, response.GetType());
            Assert.True(json.GetProperty("ok").GetBoolean());
            Assert.Equal("1", json.GetProperty("apiVersion").GetString());
            var auditTask = (Task)method.Invoke(client, ["audit.publish", FoundationTests.Event(), CancellationToken.None])!;
            await auditTask;
            response = auditTask.GetType().GetProperty("Result")!.GetValue(auditTask)!;
            json = JsonSerializer.SerializeToElement(response, response.GetType());
            Assert.Equal("accepted", json.GetProperty("data").GetProperty("status").GetString());
        }
        finally { producerContext.Unload(); hostContext.Unload(); }
    }

    [Fact]
    public async Task DispatcherHasNoFalseNotificationAcceptanceAndRejectsBadEnvelopes()
    {
        await using var f = new StoreFixture();
        var dispatcher = new ProducerDispatcher(f.Store);
        async Task<JsonElement> Send(string operation, string version = "1") => JsonSerializer.Deserialize<JsonElement>(
            await dispatcher.DispatchAsync(JsonSerializer.Serialize(new { apiVersion = version, operation,
                requestId = Guid.NewGuid(), payload = new { } }), CancellationToken.None));
        Assert.True((await Send("hello")).GetProperty("ok").GetBoolean());
        Assert.False((await Send("health")).GetProperty("data").GetProperty("ready").GetBoolean());
        Assert.Equal("unavailable", (await Send("notifications.publish")).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("unsupported_operation", (await Send("unknown")).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("invalid_request", (await Send("hello", "2")).GetProperty("error").GetProperty("code").GetString());
        var bad = JsonSerializer.Deserialize<JsonElement>(await dispatcher.DispatchAsync("{", CancellationToken.None));
        Assert.Equal("invalid_request", bad.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task NotificationAuditHookUsesOnlyFrameworkJsonAcrossAnIsolatedSdk()
    {
        await using var fixture = new StoreFixture();
        await fixture.InitializeAsync();
        var registrations = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(registrations, null!);
        registrations.AddSingleton(fixture.Store);
        using var services = registrations.BuildServiceProvider();
        var callback = services.GetKeyedService<Func<string, CancellationToken, Task<string>>>("Notifications.HubAudit.1");
        Assert.NotNull(callback);
        var context = new IsolatedContext("notification-audit-producer", ["AccountHub.Api"]);
        try
        {
            var sdk = context.LoadFromAssemblyPath(typeof(AccountHub.Api.AuditEvent).Assembly.Location);
            var type = sdk.GetType("AccountHub.Api.AuditEvent")!;
            Assert.NotSame(typeof(AccountHub.Api.AuditEvent), type);
            var json = JsonSerializer.Serialize(FoundationTests.Event(), Wire.Options);
            var foreign = JsonSerializer.Deserialize(json, type, AccountHub.Api.Contract.Json)!;
            json = JsonSerializer.Serialize(foreign, type, AccountHub.Api.Contract.Json);
            using var first = JsonDocument.Parse(await callback(json, CancellationToken.None));
            using var repeated = JsonDocument.Parse(await callback(json, CancellationToken.None));
            Assert.Equal("accepted", first.RootElement.GetProperty("status").GetString());
            Assert.Equal("duplicate", repeated.RootElement.GetProperty("status").GetString());
            Assert.Equal(first.RootElement.GetProperty("auditId").GetGuid(), repeated.RootElement.GetProperty("auditId").GetGuid());
            Assert.True((await fixture.Store.VerifyAuditAsync()).Valid);
        }
        finally { context.Unload(); }
    }

    [Fact]
    public void WireNamesSurviveHostPascalCaseSerializerAndMetadataOrderIsStable()
    {
        var json = JsonSerializer.SerializeToElement(ApiResponse.Success(new { status = "accepted" }),
            new JsonSerializerOptions { PropertyNamingPolicy = null });
        Assert.True(json.TryGetProperty("apiVersion", out _));
        Assert.True(json.TryGetProperty("ok", out _));
        Assert.Equal(CanonicalJson.Normalize(JsonSerializer.Deserialize<JsonElement>("{\"b\":2,\"a\":1}")),
            CanonicalJson.Normalize(JsonSerializer.Deserialize<JsonElement>("{\"a\":1,\"b\":2}")));
    }

    [Fact]
    public async Task AdditiveFieldsAreToleratedButBoundToTheReceiptHash()
    {
        await using var f = new StoreFixture();
        await f.InitializeAsync();
        var dispatcher = new ProducerDispatcher(f.Store);
        var value = JsonSerializer.SerializeToNode(FoundationTests.Event(), Wire.Options)!.AsObject();
        value["futureOptionalField"] = "one";
        async Task<JsonElement> Send() => JsonSerializer.Deserialize<JsonElement>(await dispatcher.DispatchAsync(
            JsonSerializer.Serialize(new ApiRequest("1", "audit.publish", Guid.NewGuid(), JsonSerializer.SerializeToElement(value))), CancellationToken.None));
        Assert.Equal("accepted", (await Send()).GetProperty("data").GetProperty("status").GetString());
        Assert.Equal("duplicate", (await Send()).GetProperty("data").GetProperty("status").GetString());
        value["futureOptionalField"] = "two";
        Assert.Equal("rejected", (await Send()).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task InvalidOrOversizeRequestsNeverGetAccepted()
    {
        await using var f = new StoreFixture();
        var dispatcher = new ProducerDispatcher(f.Store);
        foreach (var input in new[] { new string(' ', Wire.MaxBatchBytes + 1),
            "{\"apiVersion\":\"1\",\"apiVersion\":\"2\"}", "null", "[]" })
        {
            var response = JsonSerializer.Deserialize<ApiResponse>(await dispatcher.DispatchAsync(input, CancellationToken.None), Wire.Options)!;
            Assert.False(response.Ok);
            Assert.Equal("invalid_request", response.Error!.Code);
        }
    }

    [Fact]
    public void PluginIdentityAndEmbeddedPageAreFixed()
    {
        Assert.Equal("bc9c3d3e-6da3-4ee5-91cd-c429d58ad901", Plugin.PluginId);
        Assert.Contains("AccountHub.Configuration.configPage.html", typeof(Plugin).Assembly.GetManifestResourceNames());
        var policy = typeof(AccountHub.Api.AdminController).GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>();
        Assert.Equal(MediaBrowser.Common.Api.Policies.RequiresElevation, policy!.Policy);
    }

    private sealed class IsolatedContext(string name, string[] privateAssemblies) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName name) => privateAssemblies.Contains(name.Name)
            ? LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, name.Name + ".dll")) : null;
    }
}
