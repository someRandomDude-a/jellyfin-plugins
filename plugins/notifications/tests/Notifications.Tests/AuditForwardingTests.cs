using System.Text.Json;
using AccountHub.Api;
using Jellyfin.Plugin.Notifications;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Notifications.Tests;

public sealed class AuditForwardingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "opencode", "notification-audit-wiring-" + Guid.NewGuid().ToString("N"));
    private Storage Create()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(root);
        return new(paths.Object);
    }
    private static AuditEvent Entry(string type = "syncplay.party.created") => new(1, Guid.NewGuid(), "private-syncplay", type, "success", DateTimeOffset.UtcNow);

    [Fact]
    public async Task DomainAuditUsesTheHubJsonHookWithoutCreatingASecondAuditOwner()
    {
        string? received = null; var auditId = Guid.NewGuid();
        using var services = new ServiceCollection().AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>("Notifications.HubAudit.1", (_, _) => (json, _) =>
        {
            received = json;
            return Task.FromResult(Contract.Serialize(new { status = "accepted", auditId }));
        }).BuildServiceProvider();
        var storage = Create(); var entry = Entry();
        var result = JsonSerializer.SerializeToElement(await new NativeHost(services, storage).AppendAuditAsync(entry, default), Contract.Json);
        Assert.NotNull(received);
        Assert.Equal(entry.EventId, JsonSerializer.Deserialize<AuditEvent>(received!, Contract.Json)!.EventId);
        Assert.Equal(auditId, result.GetProperty("auditId").GetGuid());
        Assert.False(File.Exists(Path.Combine(storage.DirectoryPath, "audit.db")));
    }

    [Fact]
    public async Task AnUnavailableHubCannotBeSilentlyAcknowledgedByALocalFallback()
    {
        using var services = new ServiceCollection().AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>("Notifications.HubAudit.1", (_, _) => (_, _) =>
            Task.FromException<string>(new IOException("Unavailable fixture owner"))).BuildServiceProvider();
        var storage = Create();
        await Assert.ThrowsAsync<IOException>(() => new NativeHost(services, storage).AppendAuditAsync(Entry(), default));
        Assert.False(File.Exists(Path.Combine(storage.DirectoryPath, "audit.db")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"status\":\"rejected\"}")]
    [InlineData("{\"status\":true}")]
    public async Task AConfiguredOwnerMustExplicitlyAcknowledgeTheAudit(string reply)
    {
        using var services = new ServiceCollection().AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(NotificationIntegration.HubAuditKey, (_, _) => (_, _) => Task.FromResult(reply)).BuildServiceProvider();
        var storage = Create();
        await Assert.ThrowsAsync<AccountHub.Notifications.NotificationException>(() => new NativeHost(services, storage).AppendAuditAsync(Entry(), default));
        Assert.False(File.Exists(Path.Combine(storage.DirectoryPath, "audit.db")));
    }

    [Fact]
    public async Task NotificationManagementAuditsRemainIndependentOfHub()
    {
        using var services = new ServiceCollection().AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>("Notifications.HubAudit.1", (_, _) => (_, _) =>
            throw new InvalidOperationException("Notifications must retain its own audit owner")).BuildServiceProvider();
        var storage = Create(); _ = storage.Protection();
        var host = new NativeHost(services, storage); var entry = Entry("notifications.settings.changed") with { Source = "notifications" };
        var first = JsonSerializer.SerializeToElement(await host.AppendAuditAsync(entry, default), Contract.Json);
        var repeated = JsonSerializer.SerializeToElement(await host.AppendAuditAsync(entry, default), Contract.Json);
        Assert.Equal("accepted", first.GetProperty("status").GetString());
        Assert.Equal("duplicate", repeated.GetProperty("status").GetString());
        Assert.True(File.Exists(Path.Combine(storage.DirectoryPath, "audit.db")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
