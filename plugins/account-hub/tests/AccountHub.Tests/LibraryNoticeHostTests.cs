using System.Text.Json;
using DiscordBridge.Api;
using Jellyfin.LibraryNotifications;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AccountHub.Tests;

public sealed class LibraryNoticeHostTests
{
    [Fact]
    public async Task OptedOutHubDoesNotCaptureOrResolveAudienceUnlessDestinationEnablesIt()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync();
        var store = new LibraryNoticeStore(f.Paths.Database); var bridgeEnabled = false;
        var services = new ServiceCollection(); services.AddSingleton(store);
        services.AddKeyedSingleton<Func<bool>>(DiscordBridgeClient.LibraryCaptureKey, (_, _) => () => bridgeEnabled);
        using var provider = services.BuildServiceProvider(); var host = new HubLibraryNotices(provider, f.Store, new(provider));
        Assert.False(host.CaptureLocally); Assert.False(host.IncludeAudience);
        bridgeEnabled = true; Assert.True(host.CaptureLocally); Assert.False(host.IncludeAudience);
        store.SaveSettings(new(0, true)); bridgeEnabled = false;
        Assert.True(host.CaptureLocally); Assert.True(host.IncludeAudience);
        store.SaveSettings(store.Settings() with { Enabled = false }); Assert.False(host.CaptureLocally); Assert.False(host.IncludeAudience);
        // No NotificationService is installed: an opted-out inbox must skip without resolving it.
        Assert.True(await host.InboxAsync(Notice(), default));
    }
    [Fact]
    public async Task BridgeForwardingNeverIncludesJellyfinAudienceIdentities()
    {
        await using var f = new StoreFixture(); await f.InitializeAsync(); string? forwarded = null;
        var services = new ServiceCollection();
        services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(DiscordBridgeClient.ServiceKey, (_, _) => (json, _) =>
        { forwarded = json; return Task.FromResult("{\"apiVersion\":\"1\",\"ok\":true}"); });
        using var provider = services.BuildServiceProvider(); var host = new HubLibraryNotices(provider, f.Store, new(provider));
        var notice = Notice(); Assert.True(await host.DiscordAsync(notice, default));
        using var doc = JsonDocument.Parse(forwarded!);
        Assert.Empty(doc.RootElement.GetProperty("payload").GetProperty("notice").Deserialize<LibraryNotice>(LibraryNoticeFactory.Json)!.Audience);
        Assert.DoesNotContain(notice.Audience[0].ToString("D"), forwarded!);
    }
    private static LibraryNotice Notice() => new(Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], "episode", "New episode available", "Disposable metadata", DateTimeOffset.UtcNow, [Guid.NewGuid()]);
}
