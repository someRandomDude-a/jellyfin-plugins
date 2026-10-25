using AccountHub.Api;
using Jellyfin.Plugin.Notifications;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Notifications.Tests;

public sealed class PermissionRoutingTests
{
    [Fact] public async Task DeclarativeRegistrationAndCustomAccessUseOnlyFrameworkDescriptors()
    {
        var services = new ServiceCollection();
        services.RegisterNotificationTypes(new("example-plugin", "1", [new("example.updated", "Example", "Example changes", "explicit-users", new(), [])]));
        var allowed = true;
        services.RegisterNotificationAccess("example-plugin", _ => (_, _, _) => Task.FromResult(allowed));
        using var provider = services.BuildServiceProvider();
        var storage = new Storage(new Mock<IApplicationPaths>().Object); var host = new NativeHost(provider, storage);
        Assert.Equal("example-plugin", host.NotificationRegistrations.Single().Source);
        Assert.True(host.NotificationRegistrations.Single().RequireAccess);
        var user = Guid.NewGuid(); var entry = new NotificationEvent(1, Guid.NewGuid(), "example-plugin", "example.updated", "info", "Update", "Summary", [user], false, DateTimeOffset.UtcNow);
        Assert.True(await host.CanAccessNotificationAsync(user, entry, default)); allowed = false;
        Assert.False(await host.CanAccessNotificationAsync(user, entry, default));
        using var absent = new ServiceCollection().BuildServiceProvider();
        Assert.False(await new NativeHost(absent, storage).CanAccessNotificationAsync(user, entry with { Metadata = new() { ["notificationAccess"] = "required" } }, default));
    }
    [Fact] public async Task RemovalNoticesRecheckTheOwningPluginAndFailClosedWithoutIt()
    {
        var user = Guid.NewGuid(); var request = Guid.NewGuid();
        var entry = new NotificationEvent(1, Guid.NewGuid(), "deletion-queue", "proposal.updated", "info", "Removal request", "Review request", [user], false, DateTimeOffset.UtcNow,
            Metadata: new() { ["proposalId"] = request.ToString("D") });
        var paths = new Mock<IApplicationPaths>(); var storage = new Storage(paths.Object);
        using var missing = new ServiceCollection().BuildServiceProvider();
        Assert.False(await new NativeHost(missing, storage).CanAccessNotificationAsync(user, entry, default));
        var allowed = true; var calls = 0;
        var services = new ServiceCollection();
        services.AddKeyedSingleton<Func<Guid, string, CancellationToken, Task<bool>>>("Notifications.Access.deletion-queue.1", (_, _) => (recipient, json, _) => {
            calls++; Assert.Equal(user, recipient); Assert.Contains(request.ToString("D"), json); return Task.FromResult(allowed);
        });
        using var provider = services.BuildServiceProvider(); var host = new NativeHost(provider, storage);
        Assert.True(await host.CanAccessNotificationAsync(user, entry, default)); allowed = false;
        Assert.False(await host.CanAccessNotificationAsync(user, entry, default)); Assert.Equal(2, calls);
    }
    [Fact] public void OptionalPresentationDoesNotChangeLegacyEventSerialization()
    {
        var entry = new NotificationEvent(1, Guid.NewGuid(), "deletion-queue", "proposal.updated", "info", "Update", "Summary", [Guid.NewGuid()], false, DateTimeOffset.UtcNow);
        var json = Contract.Serialize(entry); Assert.DoesNotContain("\"actions\"", json); Assert.DoesNotContain("\"details\"", json);
        Assert.DoesNotContain("\"layout\"", json);
        Assert.Contains(typeof(NotificationEvent).GetConstructors(), constructor => constructor.GetParameters().Length == 14);
        Assert.Contains(typeof(NotificationEvent).GetMethods(), method => method.Name == "Deconstruct" && method.GetParameters().Length == 14);
    }
}
